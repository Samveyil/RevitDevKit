using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace RevitDevKit.Core.Compiler
{
    public static class ScriptAssemblyLoader
    {
        private static readonly List<IDisposable> Contexts = new List<IDisposable>();
        private static readonly object Sync = new object();

        // What DevKit loaded from bytes: the DLL it is a copy of, or an empty string for a compiled script.
        // An assembly loaded from bytes has no location, so the Assemblies tab asks here where it came from.
        private static readonly ConditionalWeakTable<Assembly, string> Origins = new ConditionalWeakTable<Assembly, string>();

        public static bool TryGetOrigin(Assembly assembly, out string originalPath) => Origins.TryGetValue(assembly, out originalPath);

        private static Assembly Remember(Assembly assembly, string originalPath)
        {
            try { Origins.Add(assembly, originalPath ?? string.Empty); }
            catch (ArgumentException) { }
            return assembly;
        }

        public static Assembly Load(string name, byte[] peBytes, byte[] pdbBytes)
        {
#if NETFRAMEWORK
            return Remember(pdbBytes != null && pdbBytes.Length > 0
                ? Assembly.Load(peBytes, pdbBytes)
                : Assembly.Load(peBytes), null);
#else
            var context = new UnloadableContext(name);
            lock (Sync) Contexts.Add(context);

            using (var pe = new MemoryStream(peBytes))
            using (var pdb = pdbBytes != null && pdbBytes.Length > 0 ? new MemoryStream(pdbBytes) : null)
            {
                return Remember(context.LoadFromStream(pe, pdb), null);
            }
#endif
        }

        // Loads from a shadow copy so the original DLL stays unlocked and can be rebuilt.
        // .NET Framework (Revit 2024 and older) cannot unload it; .NET 8 uses a collectible context.
        public static LoadedAssembly LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("DLL не найдена", path);

            var original = Path.GetFullPath(path);
            var sourceDir = Path.GetDirectoryName(original);
            var shadowDir = Path.Combine(
                Path.GetTempPath(),
                "RevitDevKit",
                Path.GetFileNameWithoutExtension(original) + "_" + DateTime.Now.ToString("HHmmss_fff")
            );

            Directory.CreateDirectory(shadowDir);

            var shadowPath = Path.Combine(shadowDir, Path.GetFileName(original));
            CopyWithSiblings(sourceDir, shadowDir);

            Assembly assembly;
            IDisposable owner = null;

#if NETFRAMEWORK
            var bytes = File.ReadAllBytes(shadowPath);
            var pdbPath = Path.ChangeExtension(shadowPath, ".pdb");
            assembly = File.Exists(pdbPath)
                ? Assembly.Load(bytes, File.ReadAllBytes(pdbPath))
                : Assembly.Load(bytes);
#else
            var context = new UnloadableContext(Path.GetFileNameWithoutExtension(original), shadowDir);
            lock (Sync) Contexts.Add(context);
            owner = context;
            assembly = context.LoadFromAssemblyPath(shadowPath);
#endif
            Remember(assembly, original);

            return new LoadedAssembly
            {
                Assembly = assembly,
                OriginalPath = original,
                Owner = owner
            };
        }

        public static bool TryUnload(LoadedAssembly loaded)
        {
#if NETFRAMEWORK
            return false;
#else
            var context = loaded == null ? null : loaded.Owner as UnloadableContext;
            if (context == null) return false;

            lock (Sync) Contexts.Remove(context);
            context.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
#endif
        }

        private static void CopyWithSiblings(string sourceDir, string targetDir)
        {
            if (sourceDir.IsNullOrEmpty() || !Directory.Exists(sourceDir))
                return;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".config", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
                }
                catch { }
            }
        }

#if !NETFRAMEWORK
        private sealed class UnloadableContext : AssemblyLoadContext, IDisposable
        {
            private readonly AssemblyDependencyResolver _resolver;

            public UnloadableContext(string name, string probeDir = null)
                : base("RDK_" + name, isCollectible: true)
            {
                if (probeDir != null)
                {
                    var main = Path.Combine(probeDir, name + ".dll");
                    if (File.Exists(main)) _resolver = new AssemblyDependencyResolver(main);
                }
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (_resolver == null) return null;

                var path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path == null ? null : LoadFromAssemblyPath(path);
            }

            public void Dispose()
            {
                try { Unload(); } catch { }
            }
        }
#endif
    }

    public sealed class LoadedAssembly
    {
        public Assembly Assembly { get; set; }
        public string OriginalPath { get; set; }
        internal IDisposable Owner { get; set; }

        public string DisplayName => System.IO.Path.GetFileName(OriginalPath);
    }

    // Reloads a DLL only when the file on disk changed. On .NET Framework assemblies cannot be
    // unloaded, so reusing an unchanged one avoids leaking a copy into memory on every run.
    public static class CachedAssemblyLoader
    {
        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Sync = new object();

        public static LoadedAssembly Load(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists)
                throw new FileNotFoundException("DLL не найдена", file.FullName);

            lock (Sync)
            {
                if (Cache.TryGetValue(file.FullName, out var cached) &&
                    cached.WriteTime == file.LastWriteTimeUtc && cached.Length == file.Length)
                    return cached.Loaded;

                var loaded = ScriptAssemblyLoader.LoadFromFile(file.FullName);

                if (cached != null)
                {
                    ConsoleOutput.Debug(ScriptAssemblyLoader.TryUnload(cached.Loaded)
                        ? "Предыдущая версия " + loaded.DisplayName + " выгружена."
                        : "Загружена новая версия " + loaded.DisplayName + "; прежняя останется в памяти до перезапуска Revit.");
                }

                Cache[file.FullName] = new Entry(loaded, file.LastWriteTimeUtc, file.Length);
                return loaded;
            }
        }

        public static void Forget(string path)
        {
            var key = Path.GetFullPath(path);

            lock (Sync)
            {
                if (!Cache.TryGetValue(key, out var cached)) return;

                Cache.Remove(key);
                ScriptAssemblyLoader.TryUnload(cached.Loaded);
            }
        }

        private sealed class Entry
        {
            public Entry(LoadedAssembly loaded, DateTime writeTime, long length)
            {
                Loaded = loaded;
                WriteTime = writeTime;
                Length = length;
            }

            public LoadedAssembly Loaded { get; }
            public DateTime WriteTime { get; }
            public long Length { get; }
        }
    }
}
