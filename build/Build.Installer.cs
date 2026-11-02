using System;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

public partial class Build
{
    // WiX 7 needs its OSMF EULA accepted; 6.0.2 is the last release that does not.
    const string WixVersion = "6.0.2";

    // Windows Installer finds the installed version by this code, so it must never change.
    static readonly Guid UpgradeCode = new("FF3A455E-3A4A-4D45-9BF6-000F63522155");

    static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    AbsolutePath OutputDirectory => RootDirectory / "output";

    // Packs the Release builds of every Revit version into output\RevitDevKit-<version>.msi.
    Target Installer => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var wixFolder = InstallWix();
            var manifestFile = WriteInstallerManifest();

            DotNetRun(s => s
                .SetProjectFile(RootDirectory / "installer" / "Installer.csproj")
                .SetConfiguration("Release")
                .SetApplicationArguments(manifestFile)
                .SetProcessWorkingDirectory(RootDirectory)
                .SetProcessEnvironmentVariable("PATH", $"{wixFolder};{EnvironmentInfo.GetVariable("PATH")}"));
        });

    AbsolutePath InstallWix()
    {
        var folder = TemporaryDirectory / "wix" / WixVersion;
        if (!(folder / "wix.exe").FileExists())
            DotNetToolInstall(s => s
                .SetPackageName("wix")
                .SetVersion(WixVersion)
                .SetToolInstallationPath(folder));

        return folder;
    }

    AbsolutePath WriteInstallerManifest()
    {
        var version = $"{MajorVersion}.{MinorVersion}.{MaintenanceVersion}";
        var builds = (RootDirectory / "source" / "bin" / "Release").GetDirectories()
            .Where(folder => int.TryParse(folder.Name, out _) && (folder / $"{ProjectName}.dll").FileExists())
            .OrderBy(folder => folder.Name)
            .ToArray();

        Assert.NotEmpty(builds, "No Release builds to pack into the installer");

        var manifest = new
        {
            ProductName = "Revit DevKit",
            PackageName = ProjectName,
            ProductVersion = version,
            UpgradeCode,
            ReleaseVersion = version,
            OutputDirectory = OutputDirectory.ToString(),
            Content = builds.Select(folder => new
            {
                RevitVersion = int.Parse(folder.Name),
                Files = new object[]
                {
                    new
                    {
                        Role = "payload",
                        BasePath = OutputDirectory.GetRelativePathTo(folder).ToString(),
                        TargetPath = ProjectName,
                        Include = new[] { "**" },
                        Exclude = new[] { "**/*.pdb" }
                    },
                    new
                    {
                        Role = "addin",
                        BasePath = OutputDirectory.GetRelativePathTo(RootDirectory / "source").ToString(),
                        Include = new[] { $"{ProjectName}.addin" }
                    }
                }
            })
        };

        var manifestFile = OutputDirectory / "installer.manifest.json";
        OutputDirectory.CreateDirectory();
        manifestFile.WriteAllText(JsonSerializer.Serialize(manifest, ManifestOptions));

        return manifestFile;
    }
}
