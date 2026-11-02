using Installer;
using Installer.Layouts;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;

var manifestFile = new FileInfo(args[0]);
var manifest = manifestFile.ReadManifest();
var contentRoot = manifestFile.Directory!;
var installOrder = new MediaLayout(manifest.Content);
var revitSearch = new RevitSearch(manifest.Content);

// No check for a running Revit: every version is Revit.exe, so it would also block the versions not being installed.
// Windows Installer itself asks to close the Revit that holds the files it replaces.
var project = new Project
{
    Name = manifest.ProductName,
    OutDir = manifest.OutputDirectory,
    OutFileName = $"{manifest.ProductName}-{manifest.ReleaseVersion}",
    GUID = manifest.UpgradeCode,
    // A new product code for every build lets a rebuild of the same version replace the installed one.
    ProductId = Guid.NewGuid(),
    Version = manifest.ProductVersion,
    Platform = Platform.x64,
    Scope = InstallScope.perUser,
    UI = WUI.WixUI_FeatureTree,
    MajorUpgrade = new MajorUpgrade
    {
        AllowSameVersionUpgrades = true,
        DowngradeErrorMessage = "A newer version of [ProductName] is already installed."
    },
    BackgroundImage = @"installer\Resources\Icons\BackgroundImage.png",
    BannerImage = @"installer\Resources\Icons\BannerImage.png",
    ControlPanelInfo =
    {
        Manufacturer = "Samir Shibliev",
        HelpLink = "https://github.com/Samveyil/RevitDevKit/issues",
        UrlInfoAbout = "https://github.com/Samveyil/RevitDevKit",
        ProductIcon = @"installer\Resources\Icons\ShellIcon.ico"
    },
    Dirs = manifest.Content.CreateFeatureLayout(contentRoot, installOrder)
};

// Welcome, then straight to the choice of Revit versions.
project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);
project.WixSourceGenerated += installOrder.WriteToWixSource;
project.WixSourceGenerated += revitSearch.WriteToWixSource;

project.BuildMsi();
