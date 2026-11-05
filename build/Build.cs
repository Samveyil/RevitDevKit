using BIMPlugins.Nuke;
using Nuke.Common;
using Nuke.Common.ProjectModel;

public partial class Build : BIMPluginsBuild
{
    protected override int MajorVersion => 0;
    protected override int MinorVersion => 9;
    protected override int MaintenanceVersion => 0;


    [Solution("RevitDevKit.sln")]
    protected override Solution Solution { get; }

    [Parameter("Name of the project to build")]
    protected override string ProjectName { get; } = "RevitDevKit";

    [Parameter("Configurations to build")]
    protected override string[] Configurations { get; } = new[] { "R2019", "R2020", "R2021", "R2022", "R2023", "R2024" };

    public static int Main() => Execute<Build>(x => x.Compile);
}