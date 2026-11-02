using BIMPlugins.Nuke;
using Nuke.Common;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

public class Build : BIMPluginsBuild
{
    protected override int MajorVersion => 1;
    protected override int MinorVersion => 0;
    protected override int MaintenanceVersion => 0;


    [Solution("RevitDevKit.sln")]
    protected override Solution Solution { get; }

    [Parameter("Имя проекта для сборки")]
    protected override string ProjectName { get; } = "RevitDevKit";

    // Packs the Release builds into output\RevitDevKit-<version>.msi.
    Target Installer => _ => _
        .DependsOn(Compile)
        .Executes(() => DotNetRun(s => s
            .SetProjectFile(RootDirectory / "install" / "Installer.csproj")
            .SetApplicationArguments($"{MajorVersion}.{MinorVersion}.{MaintenanceVersion}")));

    public static int Main() => Execute<Build>(x => x.Compile);
}