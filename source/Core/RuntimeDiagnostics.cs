using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RevitDevKit.Core
{
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
                    // some assemblies cannot report their name or location
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
