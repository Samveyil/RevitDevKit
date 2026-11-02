using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;
using File = WixSharp.File;

namespace Installer
{
    // Packs the Release builds from source\bin\Release\<year> into output\RevitDevKit-<version>.msi.
    // Usage: dotnet run --project install [-- <version>]. Without a version it is read from RevitDevKit.dll.
    internal static class Program
    {
        private const string Name = "RevitDevKit";
        private const string Manufacturer = "Samir Shibliev";

        // Windows Installer finds the installed version by this code, so it must never change.
        private static readonly Guid UpgradeCode = new Guid("FF3A455E-3A4A-4D45-9BF6-000F63522155");

        // Revit loads its own copies of these, they must not end up in the add-in folder.
        private static readonly string[] RevitAssemblies = { "RevitAPI.dll", "RevitAPIUI.dll", "AdWindows.dll", "UIFramework.dll" };

        private static int Main(string[] args)
        {
            try
            {
                var root = FindRoot();
                var builds = FindBuilds(Path.Combine(root, "source", "bin", "Release"));

                foreach (var build in builds)
                    Console.WriteLine($"Revit {build.Year}: {build.Version}, собрано {build.BuiltAt:dd.MM.yyyy HH:mm}");

                var built = SameVersion(builds);
                var version = args.Length > 0 ? ParseVersion(args[0]) : built;
                return CreateProject(root, builds, version).BuildMsi() == null ? 1 : 0;
            }
            catch (SetupError ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static Project CreateProject(string root, RevitBuild[] builds, Version version)
        {
            var addin = Path.Combine(root, "source", Name + ".addin");

            // One feature per Revit version. It is checked by default when that Revit is installed.
            var years = builds.Select(build =>
            {
                var feature = new Feature("Revit " + build.Year, "Надстройка для Revit " + build.Year, false)
                {
                    Id = "R" + build.Year,
                    Condition = new FeatureCondition(RevitFound(build.Year), 1)
                };

                return new Dir(feature, build.Year.ToString(),
                    new File(feature, addin),
                    new Dir(feature, Name, new Files(feature, Path.Combine(build.Folder, "*.*"), IsPayload)));
            });

            var project = new Project(Name,
                new Dir(@"%AppDataFolder%\Autodesk\Revit\Addins", years.Cast<WixEntity>().ToArray()),
                new CloseApplication("Revit.exe")
                {
                    Description = "Закройте Revit и нажмите «Повторить».",
                    PromptToContinue = true,
                    RebootPrompt = false
                })
            {
                GUID = UpgradeCode,
                // A new product code for every build lets a rebuild of the same version replace the installed one.
                ProductId = Guid.NewGuid(),
                Version = version,
                Platform = Platform.x64,
                InstallScope = InstallScope.perUser,
                MajorUpgrade = new MajorUpgrade
                {
                    AllowSameVersionUpgrades = true,
                    DowngradeErrorMessage = "Уже установлена более новая версия [ProductName]."
                },
                UI = WUI.WixUI_FeatureTree,
                Language = "ru-RU",
                OutDir = Path.Combine(root, "output"),
                OutFileName = Name + "-" + version.ToString(3)
            };

            project.ControlPanelInfo.Manufacturer = Manufacturer;
            project.ControlPanelInfo.UrlInfoAbout = "https://github.com/Samveyil/RevitDevKit";
            project.ControlPanelInfo.ProductIcon = MakeIcon(Path.Combine(root, "source", "Resources", "DevKit_32.png"));

            // Welcome, then straight to the choice of Revit versions.
            project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);
            project.WixSourceGenerated += document => AddRevitSearch(document, builds);

            return project;
        }

        private static string RevitFound(int year) => "REVIT" + year;

        // Sets REVIT<year> to the path of Revit.exe if that Revit is installed in its default folder.
        private static void AddRevitSearch(XDocument document, IEnumerable<RevitBuild> builds)
        {
            var product = document.Root.Elements().First(e => e.Name.LocalName == "Product");
            var ns = product.Name.Namespace;

            foreach (var build in builds)
                product.Add(new XElement(ns + "Property", new XAttribute("Id", RevitFound(build.Year)),
                    new XElement(ns + "DirectorySearch",
                        new XAttribute("Id", "RevitFolder" + build.Year),
                        new XAttribute("Path", @"[ProgramFiles64Folder]Autodesk\Revit " + build.Year),
                        new XAttribute("Depth", 0),
                        new XElement(ns + "FileSearch",
                            new XAttribute("Id", "RevitExe" + build.Year),
                            new XAttribute("Name", "Revit.exe")))));
        }

        private static bool IsPayload(string file)
        {
            var name = Path.GetFileName(file);
            return !name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !RevitAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private static string FindRoot()
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                if (System.IO.File.Exists(Path.Combine(dir.FullName, Name + ".sln")))
                    return dir.FullName;

            throw new SetupError("Не найден " + Name + ".sln в папках над установщиком.");
        }

        private static RevitBuild[] FindBuilds(string releaseFolder)
        {
            var folders = Directory.Exists(releaseFolder) ? Directory.GetDirectories(releaseFolder) : new string[0];
            var builds = folders
                .Where(folder => int.TryParse(Path.GetFileName(folder), out _))
                .Where(folder => System.IO.File.Exists(Path.Combine(folder, Name + ".dll")))
                .Select(folder => new RevitBuild(folder))
                .OrderBy(build => build.Year)
                .ToArray();

            if (builds.Length == 0)
                throw new SetupError("В " + releaseFolder + " нет сборок. Соберите конфигурации R2019–R2024.");

            return builds;
        }

        // Different versions mean some of the builds are stale.
        private static Version SameVersion(RevitBuild[] builds)
        {
            var versions = builds.Select(build => build.Version).Distinct().ToArray();
            if (versions.Length > 1)
                throw new SetupError("Сборки разных версий. Пересоберите все конфигурации R2019–R2024.");

            return versions[0];
        }

        private static Version ParseVersion(string text)
        {
            if (!Version.TryParse(text, out var version))
                throw new SetupError("Неверная версия: " + text);

            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }

        // An .ico file may hold a PNG image as is, so the ribbon icon also serves Programs and Features.
        private static string MakeIcon(string png)
        {
            var image = System.IO.File.ReadAllBytes(png);
            var ico = Path.Combine(Path.GetTempPath(), Name + ".ico");

            using (var writer = new BinaryWriter(System.IO.File.Create(ico)))
            {
                writer.Write((short)0);     // reserved
                writer.Write((short)1);     // icon
                writer.Write((short)1);     // one image
                writer.Write(image[19]);    // width and height: low bytes of the PNG header fields
                writer.Write(image[23]);
                writer.Write((short)0);     // no palette, reserved
                writer.Write((short)1);     // color planes
                writer.Write((short)32);    // bits per pixel
                writer.Write(image.Length);
                writer.Write(22);           // the image follows the 6-byte header and one 16-byte entry
                writer.Write(image);
            }

            return ico;
        }

        private sealed class RevitBuild
        {
            public RevitBuild(string folder)
            {
                var dll = Path.Combine(folder, Name + ".dll");
                var info = FileVersionInfo.GetVersionInfo(dll);

                Folder = folder;
                Year = int.Parse(Path.GetFileName(folder));
                Version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                BuiltAt = System.IO.File.GetLastWriteTime(dll);
            }

            public string Folder { get; }
            public int Year { get; }
            public Version Version { get; }
            public DateTime BuiltAt { get; }
        }

        private sealed class SetupError : Exception
        {
            public SetupError(string message) : base(message) { }
        }
    }
}
