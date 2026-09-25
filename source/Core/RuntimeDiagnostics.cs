using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RevitScriptRunner.Core
{
    /// <summary>
    /// Печатает, какие версии сборок компилятора реально живут в процессе Revit.
    ///
    /// Нужно потому, что Revit тащит с собой свои копии базовых библиотек, а в
    /// Revit.exe.config могут стоять binding redirect'ы. Тогда запрос новой версии
    /// молча перенаправляется на старую, загрузка проходит — и Roslyn падает внутри,
    /// на вызове метода, которого в старой сборке нет.
    ///
    /// Этот класс намеренно не ссылается ни на один тип Roslyn: он должен работать
    /// даже когда сам Roslyn сломан.
    /// </summary>
    internal static class RuntimeDiagnostics
    {
        private static readonly string[] Watched =
        {
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp",
            "System.Collections.Immutable",
            "System.Reflection.Metadata",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Memory",
            "System.Buffers",
            "System.Numerics.Vectors",
            "System.Threading.Tasks.Extensions",
            "System.Text.Encoding.CodePages"
        };

        public static void DumpCompilerEnvironment()
        {
            ConsoleOutput.Banner("Окружение компилятора");

            foreach (var name in Watched)
            {
                ConsoleOutput.WriteLine(name);

                foreach (var line in LoadedVersions(name))
                    ConsoleOutput.WriteLine("    в процессе: " + line);

                ConsoleOutput.WriteLine("    рядом с плагином: " + FileVersion(name));
            }

            ConsoleOutput.WriteLine("Папка плагина: " + (App.PluginDirectory ?? "<неизвестно>"));
        }

        private static IEnumerable<string> LoadedVersions(string simpleName)
        {
            var found = new List<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic)
                        continue;

                    var assemblyName = assembly.GetName();
                    if (!string.Equals(assemblyName.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    found.Add(assemblyName.Version + "   " +
                              (string.IsNullOrEmpty(assembly.Location) ? "<память>" : assembly.Location));
                }
                catch
                {
                    // отдельные сборки бросают на .Location — пропускаем
                }
            }

            if (found.Count == 0) found.Add("не загружена");
            return found;
        }

        private static string FileVersion(string simpleName)
        {
            try
            {
                var path = Path.Combine(App.PluginDirectory ?? ".", simpleName + ".dll");
                if (!File.Exists(path))
                    return "файла нет";

                return AssemblyName.GetAssemblyName(path).Version.ToString();
            }
            catch (Exception ex)
            {
                return "<" + ex.GetType().Name + ">";
            }
        }
    }
}