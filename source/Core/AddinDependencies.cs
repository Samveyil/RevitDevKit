using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RevitDevKit.Core
{
    // One DLL file as its header describes it; nothing is loaded.
    public sealed class DllInfo
    {
        public string Path { get; set; }
        public string FileName { get; set; }

        // Null for a native DLL.
        public string AssemblyName { get; set; }
        public Version AssemblyVersion { get; set; }

        // Three parts, as Windows shows it: 4.600.24, 13.0.3.
        public string FileVersion { get; set; }

        public IList<AssemblyName> References { get; set; } = new List<AssemblyName>();

        public bool IsManaged => AssemblyName != null;
    }

    public enum DependencyLevel
    {
        // The same versions everywhere.
        None,
        // Only the file versions differ: one assembly version, so any copy fits every add-in.
        FileOnly,
        // The assembly versions differ: Revit loads one copy, and an add-in built against another may fail.
        Assembly
    }

    public sealed class DllCopy
    {
        // The add-in, or "Revit" for its own folder.
        public string Owner { get; set; }
        public DllInfo Dll { get; set; }
        public bool IsLoaded { get; set; }
    }

    public sealed class LoadedCopy
    {
        public string Path { get; set; }
        public string FileVersion { get; set; }
        public string Owner { get; set; }
    }

    // One DLL that several add-ins (or an add-in and Revit) bring.
    public sealed class VersionGroup
    {
        public string FileName { get; set; }
        public List<DllCopy> Copies { get; set; } = new List<DllCopy>();
        public DependencyLevel Level { get; set; }

        // What this session of Revit has loaded under this name, and from where.
        public List<LoadedCopy> Loaded { get; set; } = new List<LoadedCopy>();
    }

    public enum MissingKind
    {
        // Neither next to the add-in, nor in Revit, the GAC or anywhere else: it fails when it gets to that code.
        Nowhere,
        // Not next to the add-in, but another add-in has it or loaded it: it works while that one is there.
        Elsewhere
    }

    // A DLL an add-in references but does not ship.
    public sealed class MissingDll
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        public Version Version { get; set; }
        public string ReferencedBy { get; set; }
        public MissingKind Kind { get; set; }
        public string FoundPath { get; set; }
        public string FoundOwner { get; set; }

        // The copy found elsewhere is the one loaded now, not just lying in another add-in's folder.
        public bool FoundLoaded { get; set; }
    }

    public sealed class DependencyReport
    {
        public string RevitVersion { get; set; }
        public DateTime CheckedAt { get; set; }
        public List<VersionGroup> Groups { get; set; } = new List<VersionGroup>();
        public List<MissingDll> Missing { get; set; } = new List<MissingDll>();
    }

    // Which DLLs the add-ins of this Revit version bring, and where they disagree. An add-in's DLLs are the ones in
    // the folder of its assembly (the copies .NET finds for it); when that folder is shared, as Addins\<year> is, only
    // what its assembly references. References are followed from the add-in's assemblies to find what it uses but
    // does not ship. Revit's own folder takes part as "Revit": its copies are usually loaded first.
    public static class AddinDependencies
    {
        public const string RevitOwner = "Revit";

        // Revit's folder; tests point it elsewhere.
        public static string RevitFolder { get; set; }

        private static readonly ConcurrentDictionary<string, Tuple<DateTime, DllInfo>> Cache =
            new ConcurrentDictionary<string, Tuple<DateTime, DllInfo>>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, bool> SystemNames =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private sealed class Participant
        {
            public string Name;
            public List<string> Mains = new List<string>();
            public List<string> Folders = new List<string>();
            // File name -> path of every DLL where .NET would look for this add-in.
            public Dictionary<string, string> Index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The DLLs counted as the add-in's own.
            public HashSet<string> Own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Shared;
        }

        public static DependencyReport Scan(IEnumerable<AddinManifest> manifests, IList<Tuple<string, string>> loadedAssemblies,
            IList<string> loadedModules, CancellationToken token)
        {
            var revitFolder = (RevitFolder ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            var enabled = manifests.Where(m => m.IsEnabled && m.Error == null).ToList();

            var sharedFolders = new HashSet<string>(enabled.Select(m => Path.GetDirectoryName(m.FilePath)), StringComparer.OrdinalIgnoreCase) { revitFolder };
            var participants = new List<Participant>();
            foreach (var manifest in enabled)
            {
                // The same name in both folders is one add-in.
                var p = participants.FirstOrDefault(x => x.Name == manifest.Name);
                if (p == null) participants.Add(p = new Participant { Name = manifest.Name });

                foreach (var main in manifest.Assemblies.Where(File.Exists))
                {
                    p.Mains.Add(main);
                    var folder = Path.GetDirectoryName(main);
                    if (folder == null || p.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                    p.Folders.Add(folder);

                    // A shared folder holds other add-ins' DLLs too: only what the assembly references counts.
                    var shared = IsShared(folder, sharedFolders, revitFolder);
                    p.Shared |= shared;
                    foreach (var file in Dlls(folder, recursive: !shared))
                    {
                        if (!p.Index.ContainsKey(Path.GetFileName(file))) p.Index[Path.GetFileName(file)] = file;
                        if (!shared) p.Own.Add(file);
                    }
                }
            }

            participants.RemoveAll(p => p.Mains.Count == 0);
            token.ThrowIfCancellationRequested();

            var report = new DependencyReport { RevitVersion = AddinManifests.RevitVersion, CheckedAt = DateTime.Now };
            var revitIndex = Dlls(revitFolder, recursive: false).ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);
            var loadedByName = loadedAssemblies
                .GroupBy(a => a.Item1, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(a => a.Item2).ToList(), StringComparer.OrdinalIgnoreCase);

            string OwnerOf(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                var owner = participants.FirstOrDefault(p => p.Own.Contains(path) || !p.Shared && p.Folders.Any(f => IsUnder(path, f)));
                if (owner != null) return owner.Name;
                if (string.Equals(Path.GetDirectoryName(path), revitFolder, StringComparison.OrdinalIgnoreCase)) return RevitOwner;
                return path.IndexOf(@"\assembly\GAC", StringComparison.OrdinalIgnoreCase) >= 0 ? "GAC" : null;
            }

            // Follows references from the add-in's assemblies: what is found next to it is its own (that is how a shared
            // folder gets its DLLs), what is not is looked for where .NET and other add-ins would find it.
            foreach (var p in participants)
            {
                var queue = new Queue<string>(p.Mains);
                var seen = new HashSet<string>(p.Mains, StringComparer.OrdinalIgnoreCase);
                foreach (var main in p.Mains) p.Own.Add(main);

                while (queue.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var dll = Read(queue.Dequeue());
                    foreach (var reference in dll.References)
                    {
                        var file = reference.Name + ".dll";
                        if (p.Index.TryGetValue(file, out var local) || p.Index.TryGetValue(reference.Name + ".exe", out local))
                        {
                            p.Own.Add(local);
                            if (seen.Add(local)) queue.Enqueue(local);
                            continue;
                        }

                        if (IsSystem(reference.Name) || revitIndex.ContainsKey(file)) continue;
                        if (report.Missing.Any(m => m.Owner == p.Name && string.Equals(m.Name, reference.Name, StringComparison.OrdinalIgnoreCase))) continue;

                        var missing = new MissingDll { Owner = p.Name, Name = reference.Name, Version = reference.Version, ReferencedBy = dll.Path, Kind = MissingKind.Nowhere };
                        if (loadedByName.TryGetValue(reference.Name, out var loadedPaths))
                        {
                            var owner = loadedPaths.Select(OwnerOf).FirstOrDefault(o => o != null && o != p.Name);
                            if (owner == RevitOwner || owner == "GAC") continue;
                            missing.Kind = MissingKind.Elsewhere;
                            missing.FoundOwner = owner;
                            missing.FoundPath = loadedPaths.First();
                            missing.FoundLoaded = true;
                        }
                        else
                        {
                            var other = participants.FirstOrDefault(o => o != p && o.Index.ContainsKey(file));
                            if (other != null)
                            {
                                missing.Kind = MissingKind.Elsewhere;
                                missing.FoundOwner = other.Name;
                                missing.FoundPath = other.Index[file];
                            }
                        }

                        report.Missing.Add(missing);
                    }
                }
            }

            // Every DLL by file name, from each add-in and from Revit where an add-in has it too.
            var copies = new Dictionary<string, List<DllCopy>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in participants)
            foreach (var path in p.Own)
            {
                token.ThrowIfCancellationRequested();
                Add(copies, new DllCopy { Owner = p.Name, Dll = Read(path) });
            }

            foreach (var name in copies.Keys.ToList())
                if (revitIndex.TryGetValue(name, out var revitCopy))
                    Add(copies, new DllCopy { Owner = RevitOwner, Dll = Read(revitCopy) });

            var modules = new HashSet<string>(loadedModules ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var pair in copies)
            {
                var list = pair.Value;
                if (list.Select(c => c.Owner).Distinct().Count() < 2) continue;

                var group = new VersionGroup { FileName = list[0].Dll.FileName, Copies = list.OrderBy(c => c.Owner == RevitOwner ? 0 : 1).ThenBy(c => c.Owner, StringComparer.OrdinalIgnoreCase).ToList() };
                var distinct = list.GroupBy(c => c.Dll.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First().Dll).ToList();
                group.Level = distinct.All(d => d.IsManaged) && distinct.Select(d => d.AssemblyVersion).Distinct().Count() > 1 ? DependencyLevel.Assembly
                    : distinct.Select(d => d.FileVersion).Distinct().Count() > 1 ? DependencyLevel.FileOnly
                    : DependencyLevel.None;

                var simpleName = distinct.FirstOrDefault(d => d.IsManaged)?.AssemblyName;
                var loadedPaths = simpleName != null && loadedByName.TryGetValue(simpleName, out var paths) ? paths
                    : modules.Where(m => string.Equals(Path.GetFileName(m), group.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var path in loadedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    group.Loaded.Add(new LoadedCopy { Path = path, FileVersion = File.Exists(path) ? Read(path).FileVersion : "?", Owner = OwnerOf(path) });
                    foreach (var copy in group.Copies.Where(c => string.Equals(c.Dll.Path, path, StringComparison.OrdinalIgnoreCase)))
                        copy.IsLoaded = true;
                }

                report.Groups.Add(group);
            }

            report.Groups = report.Groups.OrderByDescending(g => g.Level).ThenBy(g => g.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            report.Missing = report.Missing.OrderBy(m => m.Kind).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Owner).ToList();
            return report;
        }

        private static void Add(Dictionary<string, List<DllCopy>> copies, DllCopy copy)
        {
            if (!copies.TryGetValue(copy.Dll.FileName, out var list)) copies[copy.Dll.FileName] = list = new List<DllCopy>();
            if (!list.Any(c => c.Owner == copy.Owner && string.Equals(c.Dll.Path, copy.Dll.Path, StringComparison.OrdinalIgnoreCase)))
                list.Add(copy);
        }

        // A folder of add-in manifests, Revit's own folder or anything above it: never taken as one add-in's.
        private static bool IsShared(string folder, HashSet<string> sharedFolders, string revitFolder) =>
            sharedFolders.Contains(folder.TrimEnd('\\')) || IsUnder(revitFolder, folder) || Path.GetPathRoot(folder) == folder;

        private static bool IsUnder(string path, string folder) =>
            path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> Dlls(string folder, bool recursive)
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    : new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        // The .NET Framework and the GAC answer for these: System.*, WindowsBase, netstandard and the like.
        private static bool IsSystem(string name) => SystemNames.GetOrAdd(name, n =>
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var runtime = RuntimeEnvironment.GetRuntimeDirectory();
            return new[]
                {
                    Path.Combine(windows, @"Microsoft.NET\assembly\GAC_MSIL", n),
                    Path.Combine(windows, @"Microsoft.NET\assembly\GAC_64", n),
                    Path.Combine(windows, @"Microsoft.NET\assembly\GAC_32", n),
                    Path.Combine(windows, @"assembly\GAC_MSIL", n),
                    Path.Combine(windows, @"assembly\GAC", n)
                }.Any(Directory.Exists)
                || File.Exists(Path.Combine(runtime, n + ".dll"))
                || File.Exists(Path.Combine(runtime, "Facades", n + ".dll"))
                || File.Exists(Path.Combine(runtime, "WPF", n + ".dll"));
        });

        // The header only, read once per file version: hundreds of DLLs in Program Files are checked quickly again.
        public static DllInfo Read(string path)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { stamp = DateTime.MinValue; }

            if (Cache.TryGetValue(path, out var cached) && cached.Item1 == stamp)
                return cached.Item2;

            var info = new DllInfo { Path = path, FileName = Path.GetFileName(path), FileVersion = FileVersionOf(path) };
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var pe = new PEReader(stream))
                {
                    if (pe.HasMetadata)
                    {
                        var metadata = pe.GetMetadataReader();
                        if (metadata.IsAssembly)
                        {
                            var definition = metadata.GetAssemblyDefinition();
                            info.AssemblyName = metadata.GetString(definition.Name);
                            info.AssemblyVersion = definition.Version;
                        }

                        info.References = metadata.AssemblyReferences
                            .Select(h => metadata.GetAssemblyReference(h))
                            .Select(r => new AssemblyName { Name = metadata.GetString(r.Name), Version = r.Version })
                            .ToList();
                    }
                }
            }
            catch (Exception)
            {
                // Not a PE file or unreadable: it stays a file with a version, if it has one.
            }

            Cache[path] = Tuple.Create(stamp, info);
            return info;
        }

        private static string FileVersionOf(string path)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                if (version.FileMajorPart == 0 && version.FileMinorPart == 0 && version.FileBuildPart == 0 && version.FilePrivatePart == 0)
                    return string.IsNullOrWhiteSpace(version.FileVersion) ? "—" : version.FileVersion.Trim();
                return version.FileMajorPart + "." + version.FileMinorPart + "." + version.FileBuildPart;
            }
            catch
            {
                return "?";
            }
        }

        // Markdown for an issue or a chat: the missing DLLs, then a table of versions with a column per add-in.
        public static string ToMarkdown(DependencyReport report, bool onlyProblems)
        {
            var text = new StringBuilder();
            text.AppendLine("### Revit " + (report.RevitVersion ?? "") + ": зависимости надстроек (" + report.CheckedAt.ToString("yyyy-MM-dd HH:mm") + ")");
            text.AppendLine();

            text.AppendLine("#### Не хватает DLL");
            text.AppendLine();
            if (report.Missing.Count == 0)
            {
                text.AppendLine("Нет.");
            }
            else
            {
                Table(text, new[] { "DLL", "Версия", "Нужна", "Где есть" }, report.Missing.Select(m => new[]
                {
                    m.Name + ".dll", m.Version?.ToString() ?? "", m.Owner,
                    m.Kind == MissingKind.Nowhere ? "нигде" : (m.FoundOwner ?? "?") + ": " + m.FoundPath
                }));
            }

            text.AppendLine();
            text.AppendLine("#### Разные версии");
            text.AppendLine();
            var groups = report.Groups.Where(g => !onlyProblems || g.Level != DependencyLevel.None).ToList();
            if (groups.Count == 0)
            {
                text.AppendLine("Нет.");
                return text.ToString();
            }

            var owners = groups.SelectMany(g => g.Copies.Select(c => c.Owner)).Distinct()
                .OrderBy(o => o == RevitOwner ? 0 : 1).ThenBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            var header = new[] { "DLL", "Расхождение" }.Concat(owners).Concat(new[] { "Загружена" }).ToArray();
            Table(text, header, groups.Select(g => new[]
                {
                    g.FileName,
                    g.Level == DependencyLevel.Assembly ? "версия сборки" : g.Level == DependencyLevel.FileOnly ? "версия файла" : "нет"
                }
                .Concat(owners.Select(o => string.Join(", ", g.Copies.Where(c => c.Owner == o).Select(c => c.Dll.FileVersion).Distinct()) is var v && v.Length > 0 ? v : "-"))
                .Concat(new[] { g.Loaded.Count == 0 ? "-" : string.Join(", ", g.Loaded.Select(l => l.FileVersion + " (" + (l.Owner ?? l.Path) + ")")) })
                .ToArray()));
            return text.ToString();
        }

        private static void Table(StringBuilder text, string[] header, IEnumerable<string[]> rows)
        {
            var all = new[] { header }.Concat(rows).ToList();
            var widths = Enumerable.Range(0, header.Length).Select(i => all.Max(r => r[i].Length)).ToList();
            string Line(string[] cells) => "| " + string.Join(" | ", cells.Select((c, i) => c.PadRight(widths[i]))) + " |";

            text.AppendLine(Line(header));
            text.AppendLine("|" + string.Join("|", widths.Select(w => new string('-', w + 2))) + "|");
            foreach (var row in all.Skip(1))
                text.AppendLine(Line(row));
        }
    }
}
