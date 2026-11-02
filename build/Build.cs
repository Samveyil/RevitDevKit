using BIMPlugins.Nuke;
using Nuke.Common;
using Nuke.Common.ProjectModel;

public partial class Build : BIMPluginsBuild
{
    protected override int MajorVersion => 1;
    protected override int MinorVersion => 0;
    protected override int MaintenanceVersion => 0;


    [Solution("RevitDevKit.sln")]
    protected override Solution Solution { get; }

    [Parameter("Имя проекта для сборки")]
    protected override string ProjectName { get; } = "RevitDevKit";

    [Parameter("Имена конфигураций для сборки (перечислить через запятую)")]
    protected override string[] Configurations { get; } = new[] { "R2019", "R2020", "R2021", "R2022", "R2023", "R2024" };

    public static int Main() => Execute<Build>(x => x.Compile);
}