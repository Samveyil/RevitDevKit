using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RevitDevKit.Core
{
    public sealed class CompileMessage
    {
        public bool IsError { get; set; }
        public string Id { get; set; }
        public string Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        public override string ToString()
        {
            var where = Line > 0 ? "(" + Line + "," + Column + ") " : string.Empty;
            return where + (IsError ? "ошибка " : "предупреждение ") + Id + ": " + Message;
        }
    }

    public sealed class CompileResult
    {
        public bool Success { get; set; }
        public Assembly Assembly { get; set; }
        public IList<CompileMessage> Messages { get; set; }
        public long ElapsedMs { get; set; }

        public IEnumerable<CompileMessage> Errors { get => Messages.Where(m => m.IsError); }
    }

    public static class ScriptCompiler
    {
        private static readonly Dictionary<string, MetadataReference> ReferenceCache =
            new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        private static readonly object CacheSync = new object();

        public static CompileResult Compile(string userCode)
        {
            var sw = Stopwatch.StartNew();
            var messages = new List<CompileMessage>();

            var prepared = ScriptSource.Prepare(userCode);
            var assemblyName = "RevitScript_" + Guid.NewGuid().ToString("N");

            // Build options via Default + With* rather than constructors: Revit may load another
            // Roslyn version, and constructors with many optional parameters break across versions.
            // DEBUG/TRACE are required, otherwise [Conditional] Debug.* and Trace.* calls are removed.
            var parseOptions = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Latest)
                .WithDocumentationMode(DocumentationMode.None)
                .WithPreprocessorSymbols("DEBUG", "TRACE");

            var syntaxTree = CSharpSyntaxTree.ParseText(prepared.Code, parseOptions, assemblyName + ".cs", Encoding.UTF8);

            var references = BuildReferences(prepared.ExtraReferences, messages);

            var compilation = CSharpCompilation.Create(assemblyName, new[] { syntaxTree }, references, null);

            compilation = compilation.WithOptions(compilation.Options
                .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithAllowUnsafe(true)
                .WithPlatform(Platform.X64)
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default));

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                EmitResult emitResult;
                try
                {
                    emitResult = compilation.Emit(
                        peStream,
                        pdbStream,
                        options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                }
                catch (Exception ex)
                {
                    sw.Stop();

                    ConsoleOutput.Exception(ex, "Компилятор упал:");
                    RuntimeDiagnostics.DumpCompilerEnvironment();

                    messages.Add(new CompileMessage
                    {
                        IsError = true,
                        Id = "RDK001",
                        Message = "Компилятор упал: " + ex.GetType().Name + ": " + ex.Message +
                                  " (подробности и версии сборок — выше в консоли)"
                    });
                    return Failed(messages, sw.ElapsedMilliseconds);
                }

                foreach (var d in emitResult.Diagnostics)
                {
                    if (d.Severity == DiagnosticSeverity.Hidden) continue;
                    if (d.Severity == DiagnosticSeverity.Info) continue;

                    messages.Add(ToMessage(d, prepared.LineOffset));
                }

                if (!emitResult.Success)
                {
                    sw.Stop();
                    return Failed(messages, sw.ElapsedMilliseconds);
                }

                var peBytes = peStream.ToArray();
                var pdbBytes = pdbStream.ToArray();

                Assembly assembly;
                try
                {
                    assembly = ScriptAssemblyLoader.Load(assemblyName, peBytes, pdbBytes);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    ConsoleOutput.Exception(ex, "Не удалось загрузить скомпилированную сборку:");
                    messages.Add(new CompileMessage
                    {
                        IsError = true,
                        Id = "RDK002",
                        Message = "Не удалось загрузить сборку: " + ex.Message
                    });
                    return Failed(messages, sw.ElapsedMilliseconds);
                }

                sw.Stop();
                return new CompileResult
                {
                    Success = true,
                    Assembly = assembly,
                    Messages = messages,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
        }

        private static CompileResult Failed(List<CompileMessage> messages, long ms)
        {
            return new CompileResult
            {
                Success = false,
                Messages = messages,
                ElapsedMs = ms
            };
        }

        private static CompileMessage ToMessage(Diagnostic d, int lineOffset)
        {
            var line = 0;
            var column = 0;

            if (d.Location != Location.None && d.Location.IsInSource)
            {
                var span = d.Location.GetLineSpan();
                line = span.StartLinePosition.Line + 1 - lineOffset;
                column = span.StartLinePosition.Character + 1;
                if (line < 1)
                    line = 0;
            }

            return new CompileMessage
            {
                IsError = d.Severity == DiagnosticSeverity.Error,
                Id = d.Id,
                Message = d.GetMessage(),
                Line = line,
                Column = column
            };
        }

        private static List<MetadataReference> BuildReferences(IList<string> extra, List<CompileMessage> messages)
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path)
            {
                if (path.IsNullOrEmpty()) return;
                if (!File.Exists(path)) return;
                
                var key = Path.GetFileName(path);
                if (!paths.ContainsKey(key))
                    paths[key] = path;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic)
                        continue;
                    var location = asm.Location;
                    if (location.IsNullOrEmpty())
                        continue;

                    Add(location);
                }
                catch { }
            }

            foreach (var path in PlatformAssemblies())
                Add(path);

            if (extra != null)
            {
                foreach (var item in extra)
                {
                    var resolved = ResolveReference(item, paths);
                    if (resolved == null)
                    {
                        messages.Add(new CompileMessage
                        {
                            IsError = true,
                            Id = "RDK010",
                            Message = "Не найдена ссылка из #r \"" + item + "\""
                        });
                        continue;
                    }
                    paths[Path.GetFileName(resolved)] = resolved;
                }
            }

            var result = new List<MetadataReference>(paths.Count);
            foreach (var path in paths.Values)
            {
                var reference = GetCached(path);
                if (reference != null)
                    result.Add(reference);
            }
            return result;
        }

        private static string ResolveReference(string item, Dictionary<string, string> known)
        {
            if (File.Exists(item))
                return Path.GetFullPath(item);

            var fileName = item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? item : item + ".dll";

            string hit;
            if (known.TryGetValue(fileName, out hit))
                return hit;

            var pluginDir = Path.GetDirectoryName(typeof(ScriptCompiler).Assembly.Location);
            var candidate = Path.Combine(pluginDir ?? ".", fileName);
            if (File.Exists(candidate))
                return candidate;

            var revitDir = Path.GetDirectoryName(typeof(Autodesk.Revit.DB.Document).Assembly.Location);
            candidate = Path.Combine(revitDir ?? ".", fileName);

            if (File.Exists(candidate))
                return candidate;

            foreach (var dir in PlatformSearchDirectories())
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static MetadataReference GetCached(string path)
        {
            lock (CacheSync)
            {
                MetadataReference cached;
                if (ReferenceCache.TryGetValue(path, out cached))
                    return cached;

                try
                {
                    cached = MetadataReference.CreateFromFile(path);
                }
                catch
                {
                    cached = null;
                }

                ReferenceCache[path] = cached;
                return cached;
            }
        }

        private static IEnumerable<string> PlatformAssemblies()
        {
#if NETFRAMEWORK
            var dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (dir == null) yield break;

            foreach (var name in new[] { "mscorlib.dll", "System.dll", "System.Core.dll", "System.Xml.dll",
                                         "System.Xml.Linq.dll", "System.Data.dll", "System.Drawing.dll",
                                         "System.Runtime.Serialization.dll", "System.Windows.Forms.dll" })
            {
                yield return Path.Combine(dir, name);
            }

            foreach (var searchDir in PlatformSearchDirectories())
            {
                if (searchDir.IsNullOrEmpty())
                    continue;
                var netstandard = Path.Combine(searchDir, "netstandard.dll");
                
                if (File.Exists(netstandard)) { yield return netstandard; break; }
            }
#else
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa)) yield break;

            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
#endif
        }

        private static IEnumerable<string> PlatformSearchDirectories()
        {
#if NETFRAMEWORK
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Path.Combine(programFiles, @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8");
            yield return Path.Combine(programFiles, @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2");
            yield return Path.GetDirectoryName(typeof(object).Assembly.Location);
#else
            yield return Path.GetDirectoryName(typeof(object).Assembly.Location);
#endif
        }
    }
}
