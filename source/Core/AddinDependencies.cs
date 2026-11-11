using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

        // Set for a satellite assembly: the resources of one language.
        public string Culture { get; set; }

        // Signed with a strong name: the version is part of its identity, so other versions load beside it.
        public bool IsSigned => PublicKeyToken != null;
        public string PublicKeyToken { get; set; }

        // Three parts, as Windows shows it: 4.600.24, 13.0.3. A dash when the file has no version.
        public string FileVersion { get; set; }

        public IList<AssemblyName> References { get; set; } = new List<AssemblyName>();

        public bool IsManaged => AssemblyName != null;
    }

    // In order of how much it matters.
    public enum DependencyLevel
    {
        // The same versions everywhere.
        None,
        // Signed, with different assembly versions: every add-in loads its own, side by side.
        SideBySide,
        // An add-in gets another file of the assembly version it was built against, or Revit redirects it to a
        // newer version: one copy is shared, usually fine.
        FileOnly,
        // Add-ins may get another assembly version than they were built against: not signed, so the first copy loaded
        // goes to everyone and which one that is depends on the load order; or Revit redirects one down to an older
        // version. An add-in that gets an older one may fail.
        Assembly
    }

    // What one add-in's copy means for that add-in.
    public enum CopyState
    {
        // It gets the copy it brings, or one just like it.
        Fine,
        // It gets another file of the same assembly version, or a newer version Revit redirects it to.
        Differs,
        // It gets another, older assembly version than it was built against.
        Breaks
    }

    public sealed class DllCopy
    {
        // The add-in, or "Revit" for its own folder.
        public string Owner { get; set; }
        public DllInfo Dll { get; set; }
        public bool IsLoaded { get; set; }

        // The assembly version the add-in gets: its own, or the one Revit.exe.config redirects it to.
        public Version EffectiveVersion { get; set; }
        public CopyState State { get; set; }
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

        // Why it is at its level, in a few words for the tab and the report.
        public string Reason { get; set; }

        // The version Revit.exe.config redirects some of the copies to, if it does.
        public Version RedirectedTo { get; set; }

        // What this session of Revit has loaded under this name, and from where.
        public List<LoadedCopy> Loaded { get; set; } = new List<LoadedCopy>();
    }

    public sealed class DependencyReport
    {
        public string RevitVersion { get; set; }
        public DateTime CheckedAt { get; set; }
        public List<VersionGroup> Groups { get; set; } = new List<VersionGroup>();
    }

    // Which DLLs the add-ins of this Revit version bring, and where they disagree. An add-in's DLLs are the ones in
    // the folder of its assembly (the copies .NET finds for it), one per name, the nearest to its assembly; when that
    // folder is shared, as Addins\<year> is, only what its assembly references. Revit's own folder takes part as
    // "Revit": its copies are usually loaded first. Language resources are left out: they are the same DLL in another
    // language, not another version.
    public static class AddinDependencies
    {
        public const string RevitOwner = "Revit";
        private const string NoVersion = "—";

        // Revit's folder; tests point it elsewhere.
        public static string RevitFolder { get; set; }

        private static readonly ConcurrentDictionary<string, Tuple<DateTime, DllInfo>> Cache =
            new ConcurrentDictionary<string, Tuple<DateTime, DllInfo>>(StringComparer.OrdinalIgnoreCase);

        // en, en-US, ru-RU, zh-Hans...: folders of language resources.
        private static readonly Lazy<HashSet<string>> Cultures = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(CultureInfo.GetCultures(CultureTypes.AllCultures).Select(c => c.Name).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase));

        private sealed class Participant
        {
            public string Name;
            public List<string> Mains = new List<string>();
            public List<string> Folders = new List<string>();
            // File name -> path of the DLLs next to an assembly in a shared folder.
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
                    p.Own.Add(main);
                    var folder = Path.GetDirectoryName(main);
                    if (folder == null || p.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                    p.Folders.Add(folder);

                    // A shared folder holds other add-ins' DLLs too: only what the assembly references counts.
                    if (IsShared(folder, sharedFolders, revitFolder))
                    {
                        p.Shared = true;
                        foreach (var file in Dlls(folder, recursive: false))
                            if (!p.Index.ContainsKey(Path.GetFileName(file))) p.Index[Path.GetFileName(file)] = file;
                    }
                    else
                    {
                        foreach (var file in Dlls(folder, recursive: true))
                            p.Own.Add(file);
                    }
                }
            }

            participants.RemoveAll(p => p.Mains.Count == 0);

            // In a shared folder, the add-in's DLLs are the ones its assembly references, and theirs, found next to it.
            foreach (var p in participants.Where(x => x.Shared))
            {
                var queue = new Queue<string>(p.Mains);
                while (queue.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var reference in Read(queue.Dequeue()).References)
                    {
                        if (p.Index.TryGetValue(reference.Name + ".dll", out var local) && p.Own.Add(local))
                            queue.Enqueue(local);
                    }
                }
            }

            token.ThrowIfCancellationRequested();

            var report = new DependencyReport { RevitVersion = AddinManifests.RevitVersion, CheckedAt = DateTime.Now };
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

            // One copy per add-in and name: several files of one name inside an add-in (x64\ and x86\, a stale
            // subfolder) are not a clash with itself; the one nearest to its assembly is the one .NET finds first.
            var copies = new Dictionary<string, List<DllCopy>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in participants)
            {
                token.ThrowIfCancellationRequested();
                foreach (var sameName in p.Own.Select(Read).Where(IsComparable).GroupBy(d => d.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    var nearest = sameName.OrderBy(d => Depth(d.Path, p.Folders)).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase).First();
                    Add(copies, new DllCopy { Owner = p.Name, Dll = nearest });
                }
            }

            foreach (var name in copies.Keys.ToList())
            {
                var revitCopy = Path.Combine(revitFolder, name);
                if (File.Exists(revitCopy) && IsComparable(Read(revitCopy)))
                    Add(copies, new DllCopy { Owner = RevitOwner, Dll = Read(revitCopy) });
            }

            var redirects = Redirects(Path.Combine(revitFolder, "Revit.exe.config"));
            var modules = new HashSet<string>(loadedModules ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var list in copies.Values)
            {
                if (list.Select(c => c.Owner).Distinct().Count() < 2) continue;

                var group = new VersionGroup
                {
                    FileName = list[0].Dll.FileName,
                    Copies = list.OrderBy(c => c.Owner == RevitOwner ? 0 : 1).ThenBy(c => c.Owner, StringComparer.OrdinalIgnoreCase).ToList()
                };
                var simpleName = list.FirstOrDefault(c => c.Dll.IsManaged)?.Dll.AssemblyName;
                var loadedPaths = simpleName != null && loadedByName.TryGetValue(simpleName, out var paths) ? paths
                    : modules.Where(m => string.Equals(Path.GetFileName(m), group.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var path in loadedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    group.Loaded.Add(new LoadedCopy { Path = path, FileVersion = File.Exists(path) ? Read(path).FileVersion : "?", Owner = OwnerOf(path) });
                    foreach (var copy in group.Copies.Where(c => string.Equals(c.Dll.Path, path, StringComparison.OrdinalIgnoreCase)))
                        copy.IsLoaded = true;
                }

                Evaluate(group, simpleName != null && redirects.TryGetValue(simpleName, out var ranges) ? ranges : null);
                report.Groups.Add(group);
            }

            report.Groups = report.Groups.OrderByDescending(g => g.Level).ThenBy(g => g.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            return report;
        }

        // One <bindingRedirect>: requests for versions Low..High get To instead.
        private sealed class Redirect
        {
            public Version Low;
            public Version High;
            public Version To;
        }

        // <bindingRedirect> of Revit.exe.config by assembly name. Only the versions in oldVersion are redirected:
        // "0.0.0.0-11.0.0.0" leaves 12 and 13 alone, and a signed assembly of those loads its own copy.
        private static Dictionary<string, List<Redirect>> Redirects(string configPath)
        {
            var result = new Dictionary<string, List<Redirect>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(configPath)) return result;
                foreach (var dependent in System.Xml.Linq.XDocument.Load(configPath).Descendants().Where(e => e.Name.LocalName == "dependentAssembly"))
                {
                    var name = (string)dependent.Elements().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity")?.Attribute("name");
                    if (string.IsNullOrEmpty(name)) continue;

                    foreach (var redirect in dependent.Elements().Where(e => e.Name.LocalName == "bindingRedirect"))
                    {
                        var range = ((string)redirect.Attribute("oldVersion") ?? string.Empty).Split('-');
                        if (!Version.TryParse((string)redirect.Attribute("newVersion"), out var to)
                            || !Version.TryParse(range[0].Trim(), out var low)
                            || !Version.TryParse(range[range.Length - 1].Trim(), out var high))
                            continue;

                        if (!result.TryGetValue(name, out var list)) result[name] = list = new List<Redirect>();
                        list.Add(new Redirect { Low = low, High = high, To = to });
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable config: no redirects are known.
            }

            return result;
        }

        // What each add-in gets, and so how much the group matters.
        // Native: one file per name in the process, compared by file version.
        // Not signed: .NET looks at the name only, the first copy loaded goes to everyone.
        // Signed: each assembly version (after Revit's redirects) is a separate assembly and loads once, side by side
        // with the others; only copies of the same version share a file.
        private static void Evaluate(VersionGroup group, List<Redirect> redirects)
        {
            var copies = group.Copies;
            var loaded = group.Loaded.Where(l => File.Exists(l.Path)).Select(l => Read(l.Path)).ToList();
            DllInfo Newest(IEnumerable<DllInfo> dlls) => dlls.OrderByDescending(d => d.AssemblyVersion).ThenByDescending(d => ParseVersion(d.FileVersion)).First();

            if (!copies.All(c => c.Dll.IsManaged) || !copies.All(c => c.Dll.IsSigned))
            {
                // One copy goes to everyone: the one loaded now, or, before anything is loaded, most likely the newest.
                // A newer one is what an add-in can live with if the library only added things (orange); an older one
                // may lack what it calls (red). Which copy wins depends on the order add-ins load, so different assembly
                // versions stay a conflict even when this session went well.
                var native = !copies.All(c => c.Dll.IsManaged);
                var reference = loaded.FirstOrDefault() ?? Newest(copies.Select(c => c.Dll));
                foreach (var copy in copies)
                {
                    copy.EffectiveVersion = reference.AssemblyVersion;
                    copy.State = !native && copy.Dll.AssemblyVersion > reference.AssemblyVersion ? CopyState.Breaks
                        : !native && copy.Dll.AssemblyVersion < reference.AssemblyVersion ? CopyState.Differs
                        : copy.Dll.FileVersion != reference.FileVersion ? CopyState.Differs
                        : CopyState.Fine;
                }

                group.Level = !native && copies.Select(c => c.Dll.AssemblyVersion).Distinct().Count() > 1 ? DependencyLevel.Assembly
                    : copies.Any(c => c.State == CopyState.Differs) ? DependencyLevel.FileOnly
                    : DependencyLevel.None;
                group.Reason = group.Level == DependencyLevel.Assembly ? "не подписана, одна копия на всех"
                    : group.Level == DependencyLevel.FileOnly ? (native ? "разные версии файла" : "одна версия сборки, разные файлы")
                    : "одинаковые версии";
                return;
            }

            foreach (var copy in copies)
                copy.EffectiveVersion = redirects?.FirstOrDefault(r => copy.Dll.AssemblyVersion >= r.Low && copy.Dll.AssemblyVersion <= r.High)?.To
                                        ?? copy.Dll.AssemblyVersion;

            var versions = copies.GroupBy(c => c.EffectiveVersion).ToList();
            string filesDiffer = null;
            foreach (var version in versions)
            {
                // The file of this version everyone gets: the one loaded now, or the newest of the copies that are it.
                var own = version.Where(c => c.Dll.AssemblyVersion == version.Key).Select(c => c.Dll).ToList();
                var reference = loaded.FirstOrDefault(l => l.AssemblyVersion == version.Key) ?? (own.Count > 0 ? Newest(own) : null);
                foreach (var copy in version)
                {
                    copy.State = copy.EffectiveVersion < copy.Dll.AssemblyVersion ? CopyState.Breaks
                        : copy.EffectiveVersion > copy.Dll.AssemblyVersion ? CopyState.Differs
                        : reference != null && copy.Dll.FileVersion != reference.FileVersion ? CopyState.Differs
                        : CopyState.Fine;
                    if (copy.State == CopyState.Differs && copy.EffectiveVersion == copy.Dll.AssemblyVersion) filesDiffer = version.Key.ToString();
                }
            }

            var redirected = copies.FirstOrDefault(c => c.EffectiveVersion != c.Dll.AssemblyVersion);
            group.RedirectedTo = redirected?.EffectiveVersion;

            if (copies.Any(c => c.State == CopyState.Breaks))
            {
                group.Level = DependencyLevel.Assembly;
                group.Reason = "Revit перенаправляет на более старую " + copies.First(c => c.State == CopyState.Breaks).EffectiveVersion;
            }
            else if (copies.Any(c => c.State == CopyState.Differs))
            {
                group.Level = DependencyLevel.FileOnly;
                group.Reason = filesDiffer == null ? "Revit перенаправляет на " + group.RedirectedTo
                    : versions.Count > 1 ? "у версии " + filesDiffer + " разные файлы"
                    : "одна версия сборки, разные файлы";
            }
            else if (versions.Count > 1)
            {
                group.Level = DependencyLevel.SideBySide;
                group.Reason = "подписана, каждая версия загрузится отдельно";
            }
            else
            {
                group.Level = DependencyLevel.None;
                group.Reason = "одинаковые версии";
            }
        }

        private static Version ParseVersion(string text) => Version.TryParse(text, out var version) ? version : new Version(0, 0);

        // Why the DLL is where it is, in a few words for the tab and the report.
        public static string Reason(VersionGroup group) => group.Reason ?? string.Empty;

        // A language's resources (X.resources.dll, a culture in the header) are not another version of anything,
        // and a native DLL without a version has nothing to compare.
        private static bool IsComparable(DllInfo dll) =>
            !dll.FileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(dll.Culture)
            && (dll.IsManaged || dll.FileVersion != NoVersion);

        // How many folders below the add-in's own folder the file lies.
        private static int Depth(string path, List<string> folders)
        {
            var folder = folders.FirstOrDefault(f => IsUnder(path, f));
            if (folder == null) return int.MaxValue;
            return path.Substring(folder.TrimEnd('\\').Length + 1).Count(c => c == '\\');
        }

        private static void Add(Dictionary<string, List<DllCopy>> copies, DllCopy copy)
        {
            if (!copies.TryGetValue(copy.Dll.FileName, out var list)) copies[copy.Dll.FileName] = list = new List<DllCopy>();
            if (!list.Any(c => c.Owner == copy.Owner))
                list.Add(copy);
        }

        // A folder of add-in manifests, Revit's own folder or anything above it: never taken as one add-in's.
        private static bool IsShared(string folder, HashSet<string> sharedFolders, string revitFolder) =>
            sharedFolders.Contains(folder.TrimEnd('\\')) || IsUnder(revitFolder, folder) || Path.GetPathRoot(folder) == folder;

        private static bool IsUnder(string path, string folder) =>
            path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // The DLLs of a folder; below it, all but the language folders (en-US\, ru-RU\...).
        private static IEnumerable<string> Dlls(string folder, bool recursive)
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(folder)) return result;
                result.AddRange(Directory.GetFiles(folder, "*.dll"));
                if (recursive)
                {
                    foreach (var sub in Directory.GetDirectories(folder))
                        if (!Cultures.Value.Contains(Path.GetFileName(sub)))
                            result.AddRange(Dlls(sub, recursive: true));
                }
            }
            catch (Exception)
            {
                // A folder without access: what was read so far.
            }

            return result;
        }

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
                            info.Culture = metadata.GetString(definition.Culture);
                            info.PublicKeyToken = TokenOf(metadata.GetBlobBytes(definition.PublicKey));
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

        // The token .NET shows in an assembly's full name: the last 8 bytes of the key's SHA-1, reversed. Null if unsigned.
        private static string TokenOf(byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0) return null;
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha1.ComputeHash(publicKey);
                return string.Concat(hash.Skip(hash.Length - 8).Reverse().Select(b => b.ToString("x2")));
            }
        }

        private static string FileVersionOf(string path)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                if (version.FileMajorPart == 0 && version.FileMinorPart == 0 && version.FileBuildPart == 0 && version.FilePrivatePart == 0)
                    return string.IsNullOrWhiteSpace(version.FileVersion) ? NoVersion : version.FileVersion.Trim();
                return version.FileMajorPart + "." + version.FileMinorPart + "." + version.FileBuildPart;
            }
            catch
            {
                return "?";
            }
        }

        // Markdown for an issue or a chat: a table of versions with a column per add-in, as Revit sees them now.
        public static string ToMarkdown(DependencyReport report, bool onlyProblems)
        {
            var text = new StringBuilder();
            text.AppendLine("### Revit " + (report.RevitVersion ?? "") + ": версии DLL у надстроек (" + report.CheckedAt.ToString("yyyy-MM-dd HH:mm") + ")");
            text.AppendLine();

            var groups = report.Groups.Where(g => !onlyProblems || g.Level == DependencyLevel.Assembly || g.Level == DependencyLevel.FileOnly).ToList();
            if (groups.Count == 0)
            {
                text.AppendLine("Расхождений нет.");
                return text.ToString();
            }

            var owners = groups.SelectMany(g => g.Copies.Select(c => c.Owner)).Distinct()
                .OrderBy(o => o == RevitOwner ? 0 : 1).ThenBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            var header = new[] { "DLL", "Почему" }.Concat(owners).Concat(new[] { "Загружена" }).ToArray();
            Table(text, header, groups.Select(g => new[] { g.FileName, Reason(g) }
                .Concat(owners.Select(o => g.Copies.FirstOrDefault(c => c.Owner == o)?.Dll.FileVersion ?? "-"))
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
