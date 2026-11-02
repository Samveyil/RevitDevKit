using WixSharp;
using File = WixSharp.File;

namespace Installer.Layouts;

/// <summary>
///     Provides extension methods for a list of <see cref="Manifest.AddinContent" /> to lay the installation out as one feature per Revit version.
/// </summary>
public static class FeatureLayout
{
    private const string AddinsRoot = @"%AppDataFolder%\Autodesk\Revit\Addins";

    /// <summary>
    ///     Creates the <see cref="Dir" /> tree installing the add-in of every Revit version.
    /// </summary>
    /// <param name="content">The add-in content to lay out.</param>
    /// <param name="contentRoot">The directory the content sources are resolved against.</param>
    /// <param name="installOrder">The order the file sets install in.</param>
    /// <returns>The <see cref="Dir" /> array the package installs.</returns>
    /// <exception cref="FileNotFoundException">A file set points to a source that is absent.</exception>
    public static Dir[] CreateFeatureLayout(this IReadOnlyList<Manifest.AddinContent> content, DirectoryInfo contentRoot, MediaLayout installOrder)
    {
        var versionDirectories = content
            .Select(addin => CreateVersionDirectory(addin, contentRoot, installOrder))
            .Cast<WixEntity>()
            .ToArray();

        return [new Dir(AddinsRoot, versionDirectories)];
    }

    /// <summary>
    ///     Creates the directory holding the add-in of a single Revit version.
    /// </summary>
    /// <remarks>The feature is checked by default when <see cref="RevitSearch" /> finds that Revit installed.</remarks>
    private static Dir CreateVersionDirectory(Manifest.AddinContent addin, DirectoryInfo contentRoot, MediaLayout installOrder)
    {
        var revitVersion = addin.RevitVersion.ToString();
        var feature = new Feature($"Revit {revitVersion}", $"Install add-in for Revit {revitVersion}", false)
        {
            Id = $"R{revitVersion}",
            Condition = new FeatureCondition(RevitSearch.PropertyName(addin.RevitVersion), 1)
        };

        var fileSets = addin.Files
            .Select(fileSet => CreateFiles(fileSet, feature, contentRoot, installOrder))
            .ToArray();

        return new Dir(feature, revitVersion, fileSets);
    }

    /// <summary>
    ///     Creates the files of a single file set and assigns them to the specified feature and cabinet.
    /// </summary>
    private static WixEntity CreateFiles(Manifest.FileSet fileSet, Feature feature, DirectoryInfo contentRoot, MediaLayout installOrder)
    {
        var source = Path.GetFullPath(Path.Combine(contentRoot.FullName, fileSet.Source));
        var diskId = $"DiskId={installOrder.ResolveDiskId(fileSet.Role)}";

        WixEntity files;
        if (Directory.Exists(source))
        {
            files = new Files(feature, Path.Combine(source, "*.*")) { OnProcess = file => file.AttributesDefinition = diskId };
        }
        else if (System.IO.File.Exists(source))
        {
            files = new File(feature, source) { AttributesDefinition = diskId };
        }
        else
        {
            throw new FileNotFoundException($"The {fileSet.Role} file set points to a missing source", source);
        }

        return fileSet.TargetPath.Length == 0 ? files : new Dir(feature, fileSet.TargetPath, files);
    }
}
