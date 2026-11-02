using System.Xml.Linq;

namespace Installer.Layouts;

/// <summary>
///     Represents the search for the installed Revit versions the package targets.
/// </summary>
/// <remarks>
///     Every found version sets its property, and the feature of that version is checked by default.
///     The search looks at the default Revit folder only; a Revit installed elsewhere is checked by hand.
/// </remarks>
public sealed class RevitSearch(IReadOnlyList<Manifest.AddinContent> content)
{
    /// <summary>
    ///     Resolves the property that holds the path of Revit.exe when the specified Revit version is installed.
    /// </summary>
    /// <param name="revitVersion">The four-digit Revit release year.</param>
    public static string PropertyName(int revitVersion) => $"REVIT{revitVersion}";

    /// <summary>
    ///     Writes the searches into the WiX source the project generates.
    /// </summary>
    /// <param name="document">The generated WiX source document.</param>
    /// <exception cref="InvalidDataException">The document holds no package.</exception>
    public void WriteToWixSource(XDocument document)
    {
        var package = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Package");
        if (package is null)
        {
            throw new InvalidDataException("The generated WiX source holds no package");
        }

        var wixNamespace = package.Name.Namespace;
        foreach (var revitVersion in content.Select(addin => addin.RevitVersion).Distinct())
        {
            package.Add(new XElement(wixNamespace + "Property",
                new XAttribute("Id", PropertyName(revitVersion)),
                new XElement(wixNamespace + "DirectorySearch",
                    new XAttribute("Id", $"RevitFolder{revitVersion}"),
                    new XAttribute("Path", $@"[ProgramFiles64Folder]Autodesk\Revit {revitVersion}"),
                    new XAttribute("Depth", 0),
                    new XElement(wixNamespace + "FileSearch",
                        new XAttribute("Id", $"RevitExe{revitVersion}"),
                        new XAttribute("Name", "Revit.exe")))));
        }
    }
}
