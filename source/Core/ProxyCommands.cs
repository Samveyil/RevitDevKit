using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BIMPlugins.ExtStorage.Extensions;

namespace RevitDevKit.Core
{
    // Revit loads a button's command from the DLL path stored in it, and a second DLL with the
    // same assembly name resolves to the one already loaded. So buttons taken from a fresh build
    // point to a small generated proxy assembly, and every proxy class forwards to the command
    // type of that build's copy.
    public static class ProxyCommands
    {
        private static readonly List<Binding> Bindings = new List<Binding>();
        private static int _generation;

        private sealed class Binding
        {
            public Type Command;
            public Type Availability;
            public IExternalCommandAvailability AvailabilityInstance;
        }

        internal static int Rebind(IEnumerable<PushButton> buttons, RibbonPreview.ShadowFolder folder, string originalDirectory)
        {
            var targets = new List<(PushButton Button, int Id, bool HasAvailability)>();

            foreach (var button in buttons)
            {
                var path = ShadowPath(button.AssemblyName, folder, originalDirectory);
                if (path == null) continue;

                try
                {
                    var assembly = folder.Load(path);
                    var command = assembly.GetType(button.ClassName);
                    if (command == null)
                    {
                        ConsoleOutput.Warning("Кнопка «" + button.Name + "»: класс " + button.ClassName + " не найден в " + Path.GetFileName(path) + ".");
                        continue;
                    }

                    var availability = button.AvailabilityClassName.IsNullOrEmpty()
                        ? null
                        : assembly.GetType(button.AvailabilityClassName);

                    Bindings.Add(new Binding { Command = command, Availability = availability });
                    targets.Add((button, Bindings.Count - 1, availability != null));
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Warning("Кнопка «" + button.Name + "» не перенаправлена: " + ex.Message);
                }
            }

            if (targets.Count == 0) return 0;

            // The name must never repeat: LoadFrom returns an already loaded assembly with the same name.
            var ns = "RevitDevKit.Proxies.G" + ++_generation + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var proxyPath = Path.Combine(folder.Directory, ns + ".dll");

            Emit(ns, targets.Select(t => t.Id), proxyPath);

            foreach (var (button, id, hasAvailability) in targets)
            {
                button.AssemblyName = proxyPath;
                button.ClassName = ns + "." + ClassName(id);
                if (hasAvailability)
                    button.AvailabilityClassName = ns + "." + ClassName(id);
            }

            return targets.Count;
        }

        public static Result Execute(int id, ExternalCommandData data, ref string message, ElementSet elements)
        {
            var binding = Bindings[id];

            try
            {
                var command = (IExternalCommand)Activator.CreateInstance(binding.Command);
                return command.Execute(data, ref message, elements);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                ConsoleOutput.Exception(real, binding.Command.FullName + " упала:");
                message = real.Message;
                return Result.Failed;
            }
        }

        public static bool IsAvailable(int id, UIApplication app, CategorySet categories)
        {
            var binding = Bindings[id];

            try
            {
                binding.AvailabilityInstance ??= (IExternalCommandAvailability)Activator.CreateInstance(binding.Availability);
                return binding.AvailabilityInstance.IsCommandAvailable(app, categories);
            }
            catch
            {
                return false;
            }
        }

        // Buttons usually point to the shadow copy (Assembly.Location), but a path built from
        // the original build folder is mapped to the copy too, so the original is never locked.
        private static string ShadowPath(string assemblyPath, RibbonPreview.ShadowFolder folder, string originalDirectory)
        {
            if (assemblyPath.IsNullOrEmpty()) return null;
            if (folder.Owns(assemblyPath)) return Path.GetFullPath(assemblyPath);

            var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.Equals(dir, originalDirectory, StringComparison.OrdinalIgnoreCase)) return null;

            var mapped = Path.Combine(folder.Directory, Path.GetFileName(assemblyPath));
            return File.Exists(mapped) ? mapped : null;
        }

        private static string ClassName(int id) => "Command" + id;

        private static void Emit(string ns, IEnumerable<int> ids, string path)
        {
            var source = new StringBuilder();
            source.AppendLine("namespace " + ns + " {");

            foreach (var id in ids)
            {
                source.AppendLine("[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]");
                source.AppendLine("public sealed class " + ClassName(id) + " : Autodesk.Revit.UI.IExternalCommand, Autodesk.Revit.UI.IExternalCommandAvailability {");
                source.AppendLine("  public Autodesk.Revit.UI.Result Execute(Autodesk.Revit.UI.ExternalCommandData d, ref string m, Autodesk.Revit.DB.ElementSet e)");
                source.AppendLine("    => RevitDevKit.Core.ProxyCommands.Execute(" + id + ", d, ref m, e);");
                source.AppendLine("  public bool IsCommandAvailable(Autodesk.Revit.UI.UIApplication a, Autodesk.Revit.DB.CategorySet c)");
                source.AppendLine("    => RevitDevKit.Core.ProxyCommands.IsAvailable(" + id + ", a, c);");
                source.AppendLine("}");
            }

            source.AppendLine("}");

            var references = new[]
                {
                    typeof(object).Assembly, typeof(Document).Assembly, typeof(UIApplication).Assembly, typeof(ProxyCommands).Assembly
                }
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));

            var compilation = CSharpCompilation.Create(ns, new[] { CSharpSyntaxTree.ParseText(source.ToString()) }, references, null);
            compilation = compilation.WithOptions(compilation.Options
                .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
                .WithPlatform(Platform.X64));

            var result = compilation.Emit(path);
            if (!result.Success)
            {
                throw new InvalidOperationException("Не удалось собрать прокси-команды:\n" +
                    string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }
        }
    }
}
