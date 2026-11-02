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


var project = new Project
{
    Name = manifest.ProductName,
    OutDir = manifest.OutputDirectory,
    OutFileName = $"{manifest.ProductName}-{manifest.ReleaseVersion}",
    GUID = manifest.UpgradeCode,
    Version = manifest.ProductVersion,
    Platform = Platform.x64,
    Scope = InstallScope.perUser,
    UI = WUI.WixUI_FeatureTree,
    MajorUpgrade = MajorUpgrade.Default,
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

project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);
project.WixSourceGenerated += installOrder.WriteToWixSource;
project.WixSourceGenerated += revitSearch.WriteToWixSource;

project.BuildMsi();
