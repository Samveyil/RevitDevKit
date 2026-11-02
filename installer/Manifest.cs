namespace Installer;

/// <summary>
///     Represents the content and identity of the installer package.
/// </summary>
/// <remarks>
///     The build writes the manifest and carries what changes from release to release.
///     The presentation, the target platform, and the directory layout are fixed by the installer.
/// </remarks>
public sealed record Manifest
{
    /// <summary>
    ///     Gets the product name shown by the setup dialogs and Programs and Features.
    /// </summary>
    public required string ProductName { get; init; }

    /// <summary>
    ///     Gets the name the package file starts with.
    /// </summary>
    public required string PackageName { get; init; }

    /// <summary>
    ///     Gets the version Windows Installer compares against the installed package when it resolves an upgrade.
    /// </summary>
    /// <remarks>The comparison covers the major, minor, and build components.</remarks>
    public required Version ProductVersion { get; init; }

    /// <summary>
    ///     Gets the identity shared by every release of the product.
    /// </summary>
    /// <remarks>
    ///     A release published under the installed value upgrades it in place.
    ///     A release published under a new value installs alongside its predecessor.
    /// </remarks>
    public required Guid UpgradeCode { get; init; }

    /// <summary>
    ///     Gets the version the release is published under.
    /// </summary>
    /// <remarks>The package file name includes this value.</remarks>
    public required string ReleaseVersion { get; init; }

    /// <summary>
    ///     Gets the absolute path of the directory the package is written to.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    ///     Gets the add-in content the package installs, one entry per Revit version.
    /// </summary>
    public required IReadOnlyList<AddinContent> Content { get; init; }

    /// <summary>
    ///     Represents the add-in files installed for a single Revit version.
    /// </summary>
    public sealed record AddinContent
    {
        /// <summary>
        ///     Gets the Revit version the files target.
        /// </summary>
        /// <value>The four-digit Revit release year.</value>
        public required int RevitVersion { get; init; }

        /// <summary>
        ///     Gets the file sets installed for the Revit version.
        /// </summary>
        /// <remarks>The package installs the sets in the order their roles first appear in the manifest.</remarks>
        public required IReadOnlyList<FileSet> Files { get; init; }
    }

    /// <summary>
    ///     Represents a set of files selected from a source directory.
    /// </summary>
    public sealed record FileSet
    {
        /// <summary>
        ///     Gets the name of the installation stage the file set belongs to.
        /// </summary>
        /// <remarks>
        ///     File sets sharing a role install together, and every role installs after the roles declared before it.
        ///     A file a running application picks up belongs in a role declared after the roles holding the files it depends on.
        /// </remarks>
        public required string Role { get; init; }

        /// <summary>
        ///     Gets the source directory the patterns are matched against.
        /// </summary>
        /// <value>A path relative to the directory holding the manifest file.</value>
        public required string BasePath { get; init; }

        /// <summary>
        ///     Gets the directory the files are installed to.
        /// </summary>
        /// <value>A path relative to the Revit version directory. Defaults to the version directory itself.</value>
        public string TargetPath { get; init; } = string.Empty;

        /// <summary>
        ///     Gets the glob patterns selecting the files.
        /// </summary>
        /// <remarks>The patterns follow the <see href="https://learn.microsoft.com/dotnet/core/extensions/file-globbing">.NET file globbing</see> format.</remarks>
        public required IReadOnlyList<string> Include { get; init; }

        /// <summary>
        ///     Gets the glob patterns excluding files from the selection.
        /// </summary>
        /// <value>Defaults to an empty list.</value>
        public IReadOnlyList<string> Exclude { get; init; } = [];
    }
}
