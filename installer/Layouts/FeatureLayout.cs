using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using WixSharp;
using File = WixSharp.File;

namespace Installer.Layouts;

/// <summary>
///     Provides extension methods for a list of <see cref="Manifest.AddinContent" /> to lay the installation out as one feature per Revit version.
/// </summary>
public static class FeatureLayout
{
    private const string AddinsRoot = @"%AppDataFolder%\Autodesk\Revit\Addins";
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    ///     Creates the <see cref="Dir" /> tree installing the add-in of every Revit version.
    /// </summary>
    /// <param name="content">The add-in content to lay out.</param>
    /// <param name="contentRoot">The directory the content base paths are resolved against.</param>
    /// <param name="installOrder">The order the file sets install in.</param>
    /// <returns>The <see cref="Dir" /> array the package installs.</returns>
    /// <exception cref="DirectoryNotFoundException">A file set points to a directory that is absent.</exception>
    /// <exception cref="InvalidDataException">A file set selects no files.</exception>
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

        var files = addin.Files.SelectMany(fileSet => SelectFiles(fileSet, feature, contentRoot, installOrder));
        return new Dir(feature, revitVersion, Nest(files, feature));
    }

    /// <summary>
    ///     Selects the files of a single file set and assigns them to the specified feature and cabinet.
    /// </summary>
    /// <returns>Every file with the directory it installs to, relative to the Revit version directory.</returns>
    private static IEnumerable<(string Directory, File File)> SelectFiles(Manifest.FileSet fileSet, Feature feature, DirectoryInfo contentRoot, MediaLayout installOrder)
    {
        var basePath = Path.GetFullPath(Path.Combine(contentRoot.FullName, fileSet.BasePath));
        if (!Directory.Exists(basePath))
        {
            throw new DirectoryNotFoundException($"The {fileSet.Role} file set points to a missing directory: {basePath}");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(fileSet.Include);
        matcher.AddExcludePatterns(fileSet.Exclude);

        var matchingResult = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(basePath)));
        if (!matchingResult.HasMatches)
        {
            throw new InvalidDataException($"The {fileSet.Role} file set selects no files under: {basePath}");
        }

        var selectedFiles = matchingResult.Files
            .Select(match => match.Path)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LogSelectedFiles(fileSet, basePath, selectedFiles);

        // WixSharp Files would also add every empty subdirectory of the base path, so the files are listed one by one.
        var diskId = installOrder.ResolveDiskId(fileSet.Role);
        return selectedFiles.Select(path => (
            Path.Combine(fileSet.TargetPath, Path.GetDirectoryName(path) ?? string.Empty),
            new File(feature, Path.Combine(basePath, path)) { AttributesDefinition = $"DiskId={diskId}" }));
    }

    /// <summary>
    ///     Places the files into the directories they install to.
    /// </summary>
    private static WixEntity[] Nest(IEnumerable<(string Directory, File File)> files, Feature feature)
    {
        var entities = new List<WixEntity>();
        foreach (var group in files.GroupBy(file => SplitFirst(file.Directory).Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0)
            {
                entities.AddRange(group.Select(file => file.File));
                continue;
            }

            var nested = group.Select(file => (SplitFirst(file.Directory).Remainder, file.File));
            entities.Add(new Dir(feature, group.Key, Nest(nested, feature)));
        }

        return [.. entities];
    }

    private static (string Name, string Remainder) SplitFirst(string path)
    {
        var parts = path.Split(Separators, 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (parts[0], parts[1])
        };
    }

    /// <summary>
    ///     Writes the files of a single file set to the build log.
    /// </summary>
    private static void LogSelectedFiles(Manifest.FileSet fileSet, string basePath, string[] selectedFiles)
    {
        Console.WriteLine($"{fileSet.Role} files from {basePath} ({selectedFiles.Length}):");

        foreach (var selectedFile in selectedFiles)
        {
            Console.WriteLine($"- {selectedFile}");
        }
    }
}
