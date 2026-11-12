using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BIMPlugins.ExtStorage.Extensions;

namespace RevitDevKit.Core
{
    // Reports of Add-in Explorer go to a text file in the temp folder, shown in Explorer, rather than to the clipboard:
    // the user opens it in any editor, attaches it to an issue or keeps it.
    public static class ReportFiles
    {
        // Tests point it at their own folder and catch what would be shown in Explorer.
        public static string Folder { get; set; } = Path.Combine(Path.GetTempPath(), "RevitDevKit", "Reports");
        public static Action<string> Reveal { get; set; } = RevealInExplorer;

        // Writes the text to <name>-<Revit version>-<time>.txt and shows the file in Explorer. Returns its path.
        public static string Save(string name, string text)
        {
            Directory.CreateDirectory(Folder);
            var version = AddinManifests.RevitVersion.IsNullOrEmpty() ? string.Empty : "-Revit" + AddinManifests.RevitVersion;
            var path = Path.Combine(Folder, name + version + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(true));
            Reveal(path);
            return path;
        }

        public static void RevealInExplorer(string path)
        {
            if (!File.Exists(path)) return;
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
    }
}
