using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Compiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RevitDevKit.Core.Addins
{
    // Where a loaded assembly comes from, for the owner column and the "hide system" filter.
    public enum AssemblySource
    {
        AddIn,
        // Copies DevKit made: a DLL command or a fresh build for the ribbon, loaded from bytes or a temp folder.
        DevKit,
        // A file in some other folder.
        Other,
        Revit,
        Gac,
        Framework,
        // Dynamic or loaded from bytes by someone else: no file behind it.
        Memory
    }

    public sealed class ProcessAssembly
    {
        // The place in the AppDomain's list, roughly the load order; it stays when the list is filtered.
        public int Number { get; set; }
        public string Name { get; set; }
        public Version Version { get; set; }
        public string FullName { get; set; }

        // The file it was loaded from; null when it was loaded from bytes or made in memory.
        public string Location { get; set; }

        // For a copy DevKit loaded from bytes: the DLL it copied. Empty for a compiled script.
        public string Origin { get; set; }
        public bool IsScript { get; set; }

        public string FileVersion { get; set; }
        public string Owner { get; set; }
        public AssemblySource Source { get; set; }

        // How many assemblies of this name are loaded, this one included.
        public int SameName { get; set; }

        public bool IsDuplicate => SameName > 1;
        public bool IsSystem => Source == AssemblySource.Revit || Source == AssemblySource.Gac
                                || Source == AssemblySource.Framework || Source == AssemblySource.Memory;

        // The file "Show in Explorer" goes to: its own, or the DLL it is a copy of.
        public string FilePath => Location ?? (Origin.IsNullOrEmpty() ? null : Origin);
    }

    // Every assembly loaded into Revit's AppDomain, numbered as the AppDomain lists them, with who brought it.
    public static class ProcessAssemblies
    {
        public static List<ProcessAssembly> Collect() =>
            Collect(AppDomain.CurrentDomain.GetAssemblies(), AddinManifests.Scan(), AddinDependencies.RevitFolderPath);

        public static List<ProcessAssembly> Collect(IList<Assembly> assemblies, IEnumerable<AddinManifest> manifests, string revitFolder)
        {
            var owners = Owners(manifests);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var devKitTemp = Path.Combine(Path.GetTempPath(), "RevitDevKit");
            var list = new List<ProcessAssembly>();

            for (var i = 0; i < assemblies.Count; i++)
            {
                var assembly = assemblies[i];
                var item = new ProcessAssembly { Number = i + 1 };
                try
                {
                    var name = assembly.GetName();
                    item.Name = name.Name;
                    item.Version = name.Version;
                    item.FullName = assembly.FullName;
                }
                catch
                {
                    item.Name = assembly.ToString();
                }

                item.Location = LocationOf(assembly);
                if (item.Location != null)
                {
                    item.FileVersion = AddinDependencies.FileVersionOf(item.Location);
                    Classify(item, item.Location, owners, revitFolder, windows, devKitTemp);
                }
                else if (ScriptAssemblyLoader.TryGetOrigin(assembly, out var origin))
                {
                    item.Origin = origin;
                    item.IsScript = origin.Length == 0;
                    item.Source = AssemblySource.DevKit;
                    item.Owner = "DevKit";
                }
                else
                {
                    item.Source = AssemblySource.Memory;
                }

                list.Add(item);
            }

            foreach (var sameName in list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var count = sameName.Count();
                foreach (var item in sameName) item.SameName = count;
            }

            return list;
        }

        private static string LocationOf(Assembly assembly)
        {
            try
            {
                if (assembly.IsDynamic) return null;
                var location = assembly.Location;
                return location.IsNullOrEmpty() ? null : location;
            }
            catch
            {
                return null;
            }
        }

        // The folders of enabled add-ins, deepest first, so an add-in inside another's folder wins.
        // A folder Addins\<year> holds many add-ins: there only their main assemblies are theirs.
        private static List<Tuple<string, string, bool>> Owners(IEnumerable<AddinManifest> manifests)
        {
            var enabled = manifests.Where(m => m.IsEnabled && m.Error == null).ToList();
            var shared = new HashSet<string>(enabled.Select(m => Path.GetDirectoryName(m.FilePath)).Where(f => f != null), StringComparer.OrdinalIgnoreCase);
            var result = new List<Tuple<string, string, bool>>();
            foreach (var manifest in enabled)
            foreach (var main in manifest.Assemblies)
            {
                string full;
                try { full = Path.GetFullPath(main); }
                catch { continue; }

                var folder = Path.GetDirectoryName(full);
                result.Add(Tuple.Create(full, manifest.Name, true));
                if (folder != null && !shared.Contains(folder) && Path.GetPathRoot(folder) != folder)
                    result.Add(Tuple.Create(folder, manifest.Name, false));
            }

            return result.OrderByDescending(o => o.Item1.Length).ToList();
        }

        private static void Classify(ProcessAssembly item, string path, List<Tuple<string, string, bool>> owners, string revitFolder,
            string windows, string devKitTemp)
        {
            var owner = owners.FirstOrDefault(o => o.Item3
                ? string.Equals(o.Item1, path, StringComparison.OrdinalIgnoreCase)
                : AddinDependencies.IsUnder(path, o.Item1));
            if (owner != null)
            {
                item.Source = AssemblySource.AddIn;
                item.Owner = owner.Item2;
            }
            else if (!revitFolder.IsNullOrEmpty() && AddinDependencies.IsUnder(path, revitFolder))
            {
                item.Source = AssemblySource.Revit;
                item.Owner = "Revit";
            }
            else if (AddinDependencies.IsUnder(path, Path.Combine(windows, "Microsoft.NET")))
            {
                item.Source = AssemblySource.Framework;
                item.Owner = ".NET";
            }
            else if (AddinDependencies.IsUnder(path, Path.Combine(windows, "assembly")))
            {
                item.Source = AssemblySource.Gac;
                item.Owner = "GAC";
            }
            else if (AddinDependencies.IsUnder(path, devKitTemp))
            {
                item.Source = AssemblySource.DevKit;
                item.Owner = "DevKit";
            }
            else
            {
                item.Source = AssemblySource.Other;
            }
        }

        // The second line of a row: the file, or what is in memory instead.
        public static string Where(ProcessAssembly item) =>
            item.Location ?? (item.IsScript ? "в памяти · скрипт"
                : item.Origin != null ? "в памяти · копия " + item.Origin
                : "в памяти");

        // Markdown for an issue or a chat: the rows as they are shown.
        public static string ToMarkdown(IEnumerable<ProcessAssembly> items, int total)
        {
            var rows = items.ToList();
            var text = new StringBuilder();
            text.AppendLine("### Revit " + (AddinManifests.RevitVersion ?? "") + ": загруженные сборки (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                            + ", показано " + rows.Count + " из " + total + ")");
            text.AppendLine();
            AddinDependencies.Table(text, new[] { "№", "Сборка", "Версия", "Версия файла", "Чья", "Путь" },
                rows.Select(a => new[] { a.Number.ToString(), Cell(a.Name) + (a.IsDuplicate ? " (x" + a.SameName + ")" : ""), Cell(a.Version?.ToString()),
                    Cell(a.FileVersion), Cell(a.Owner), Cell(Where(a)) }), 0);
            return text.ToString();
        }

        private static string Cell(string value) => value.IsNullOrEmpty() ? "-" : value.Replace("|", "\\|");
    }
}
