using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace RevitDevKit.Core
{
    public enum AddinScope
    {
        CurrentUser,
        AllUsers
    }

    // One .addin file in a folder Revit loads add-ins from, as it stands on disk.
    public sealed class AddinManifest
    {
        public string FilePath { get; set; }

        // The file name without .addin or .addin.disable.
        public string Name { get; set; }

        public AddinScope Scope { get; set; }

        // Revit loads only *.addin; *.addin.disable is the way Addin Manager turns one off, and so does DevKit.
        public bool IsEnabled { get; set; }

        // Renaming needs the right to delete the file, which installers' files in ProgramData usually deny.
        public bool CanChange { get; set; }

        public int Applications { get; set; }
        public int Commands { get; set; }

        public IList<string> Assemblies { get; set; } = new List<string>();
        public IList<string> MissingAssemblies { get; set; } = new List<string>();

        // The XML could not be read: the reason, otherwise null.
        public string Error { get; set; }

        // The same add-in is there both on and off (X.addin and X.addin.disable): renaming would overwrite one.
        public bool HasTwin { get; set; }

        public bool IsDevKit { get; set; }

        // Autodesk's own add-ins say VendorId ADSK; their DLLs are Autodesk's business, not the user's.
        public bool IsAutodesk { get; set; }
    }

    // The .addin files of the running Revit version: the user's folder and the one for all users.
    // Turning one off renames X.addin to X.addin.disable, so Revit skips it on its next start; on renames it back.
    public static class AddinManifests
    {
        public const string Extension = ".addin";
        public const string DisabledExtension = ".addin.disable";

        // Set when DevKit starts, from Revit itself; without them the folders follow the standard layout.
        public static string CurrentUserFolder { get; set; }
        public static string AllUsersFolder { get; set; }
        public static string RevitVersion { get; set; }

        public static void Locate(string currentUserFolder, string allUsersFolder, string revitVersion)
        {
            RevitVersion = revitVersion;
            CurrentUserFolder = string.IsNullOrEmpty(currentUserFolder)
                ? StandardFolder(Environment.SpecialFolder.ApplicationData, revitVersion)
                : currentUserFolder;
            AllUsersFolder = string.IsNullOrEmpty(allUsersFolder)
                ? StandardFolder(Environment.SpecialFolder.CommonApplicationData, revitVersion)
                : allUsersFolder;
        }

        private static string StandardFolder(Environment.SpecialFolder root, string revitVersion) =>
            string.IsNullOrEmpty(revitVersion)
                ? null
                : Path.Combine(Environment.GetFolderPath(root), "Autodesk", "Revit", "Addins", revitVersion);

        public static List<AddinManifest> Scan()
        {
            var result = new List<AddinManifest>();
            result.AddRange(Scan(CurrentUserFolder, AddinScope.CurrentUser));
            result.AddRange(Scan(AllUsersFolder, AddinScope.AllUsers));
            return result;
        }

        public static IEnumerable<AddinManifest> Scan(string folder, AddinScope scope)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return Enumerable.Empty<AddinManifest>();

            var files = Directory.GetFiles(folder)
                .Where(f => IsEnabledFile(f) || IsDisabledFile(f))
                .ToList();

            var names = new HashSet<string>(files.Where(IsEnabledFile).Select(NameOf), StringComparer.OrdinalIgnoreCase);
            return files
                .Select(f => Read(f, scope, twin: !IsEnabledFile(f) && names.Contains(NameOf(f))
                                                || IsEnabledFile(f) && File.Exists(f + ".disable")))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.IsEnabled ? 0 : 1)
                .ToList();
        }

        private static bool IsEnabledFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        private static bool IsDisabledFile(string path) => path.EndsWith(DisabledExtension, StringComparison.OrdinalIgnoreCase);

        private static string NameOf(string path)
        {
            var file = Path.GetFileName(path);
            var length = IsDisabledFile(path) ? DisabledExtension.Length : Extension.Length;
            return file.Substring(0, file.Length - length);
        }

        public static AddinManifest Read(string path, AddinScope scope, bool twin = false)
        {
            var manifest = new AddinManifest
            {
                FilePath = path,
                Name = NameOf(path),
                Scope = scope,
                IsEnabled = IsEnabledFile(path),
                CanChange = CanRename(path),
                HasTwin = twin
            };

            try
            {
                var folder = Path.GetDirectoryName(path);
                var addins = XDocument.Load(path).Root?.Elements().Where(e => e.Name.LocalName == "AddIn").ToList()
                             ?? new List<XElement>();

                foreach (var addin in addins)
                {
                    var type = (string)addin.Attribute("Type") ?? string.Empty;
                    var vendor = addin.Elements().FirstOrDefault(e => e.Name.LocalName == "VendorId")?.Value.Trim();
                    if (string.Equals(vendor, "ADSK", StringComparison.OrdinalIgnoreCase))
                        manifest.IsAutodesk = true;
                    if (type.Equals("Command", StringComparison.OrdinalIgnoreCase))
                        manifest.Commands++;
                    else
                        manifest.Applications++;

                    var assembly = addin.Elements().FirstOrDefault(e => e.Name.LocalName == "Assembly")?.Value.Trim();
                    if (string.IsNullOrEmpty(assembly))
                        continue;

                    // Revit takes a relative path from the folder of the .addin file.
                    assembly = Environment.ExpandEnvironmentVariables(assembly);
                    var full = Path.IsPathRooted(assembly) ? assembly : Path.Combine(folder ?? string.Empty, assembly);
                    if (manifest.Assemblies.Contains(full, StringComparer.OrdinalIgnoreCase))
                        continue;

                    manifest.Assemblies.Add(full);
                    if (!File.Exists(full))
                        manifest.MissingAssemblies.Add(full);
                }
            }
            catch (Exception ex)
            {
                manifest.Error = ex.Message;
            }

            var devKit = typeof(AddinManifests).Assembly.Location;
            manifest.IsDevKit = manifest.Assemblies.Any(a => string.Equals(SafeFullPath(a), devKit, StringComparison.OrdinalIgnoreCase));
            return manifest;
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        // Renames the file to turn the add-in on or off and returns it read again. The rename fails rather than
        // overwrite a file with the other name, and fails as Windows says when the rights are missing.
        public static AddinManifest SetEnabled(AddinManifest manifest, bool enabled)
        {
            if (manifest.IsEnabled == enabled)
                return manifest;

            var folder = Path.GetDirectoryName(manifest.FilePath) ?? string.Empty;
            var target = Path.Combine(folder, manifest.Name + (enabled ? Extension : DisabledExtension));
            if (File.Exists(target))
                throw new IOException("Рядом уже есть " + Path.GetFileName(target) + ": сначала уберите один из двух файлов.");

            File.Move(manifest.FilePath, target);
            return Read(target, manifest.Scope);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        private const uint DeleteAccess = 0x00010000;

        // Asks Windows for the right to delete the file, which a rename needs, without changing anything.
        private static bool CanRename(string path)
        {
            try
            {
                using (var handle = CreateFile(path, DeleteAccess, FileShare.ReadWrite | FileShare.Delete,
                           IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero))
                    return !handle.IsInvalid;
            }
            catch
            {
                return false;
            }
        }
    }
}
