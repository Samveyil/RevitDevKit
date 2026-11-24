using RevitDevKit.Localization;
using System.Collections.Generic;

namespace RevitDevKit.ScriptApi
{
    public sealed class Sample
    {
        public string Title { get; set; }
        public string Code { get; set; }
    }

    // The examples of the Examples menu and the code of a new tab, in DevKit's language: English here, Russian in
    // Samples.Russian.cs. The two sets go in the same order with the same code, only the comments and texts differ.
    public static partial class Samples
    {
        public static string Starter => Language.IsRussian ? RussianStarter : EnglishStarter;

        public static IReadOnlyList<Sample> All => Language.IsRussian ? RussianSamples : EnglishSamples;

        private const string EnglishStarter =
@"// A short fragment: the add-in wraps it in a class itself.
// Doc, UIDoc, UIApp, App, ActiveView, Selection, S0, S1, S2 are available.
// So are Print() / Tx() / S() / All() / Instances() / Types() / Table() / Dump() / Compare() / Show().
// Run with F5 or Ctrl+Enter.

Print(""Document: "" + Doc.Title);
Print(""Path: "" + (Doc.PathName == """" ? ""<not saved>"" : Doc.PathName));

var walls = All<Wall>();
Print($""Walls in the model: {walls.Count}"");
";

        private static readonly Sample[] EnglishSamples =
        {
            new Sample
            {
                Title = "1. Hello, Revit",
                Code =
@"Print(""Revit "" + App.VersionNumber + "" build "" + App.VersionBuild);
Print(""Document: "" + Doc.Title);
Print(""User: "" + App.Username);
"
            },

            new Sample
            {
                Title = "2. Console.WriteLine and output levels",
                Code =
@"// A plain Console.WriteLine goes to the add-in's console too,
// so other libraries log here without any changes.
Console.WriteLine(""This is a plain Console.WriteLine"");
System.Diagnostics.Debug.WriteLine(""This is Debug.WriteLine (gray)"");

Ok(""Success is green"");
Warn(""A warning is yellow"");
Fail(""An error is red"");

for (int i = 1; i <= 5; i++)
    Console.WriteLine($""line {i} of 5"");
"
            },

            new Sample
            {
                Title = "3. Going through elements and a table",
                Code =
@"// All<type>() returns all the elements of the given class, for example All<FamilyInstance>().
// Instances(category) returns all the instances of the given category,
// for example Instances(BuiltInCategory.OST_Walls).
// Types(category) returns all the types of the given category, for example Types(BuiltInCategory.OST_Floors).
//
// A view's Id may be given as well, to take only the elements visible in that view:
// All<type>(viewId), Instances(viewId, category), Types(viewId, category).
// For example, All<Dimension>(ActiveView.Id) returns all the dimensions visible in the current view.
//
// All<type>() also has overloads that filter by category:
// All<type>(category) and All<type>(viewId, category).
// For example, All<FamilyInstance>(BuiltInCategory.OST_StructuralColumns)
// returns all the family instances of the Structural Columns category.
var walls = All<Wall>();
Print($""Walls found: {walls.Count}"");

Table(walls,
    w => w.Id,
    w => w.Name,
    w => Math.Round(w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble(), 2));
"
            },

            new Sample
            {
                Title = "4. Changing the model in a transaction",
                Code =
@"// The transaction is opened here, so keep the Auto-transaction checkbox off.
// Each Tx is one step in the Undo list. If the code inside throws, the whole step is rolled back.
var walls = All<Wall>();
if (walls.Count == 0) { Warn(""There are no walls in the model.""); return; }

// Several actions go in braces. If the code returns a value, Tx returns it too.
var marked = Tx(""Mark walls with a comment"", () =>
{
    var count = 0;
    foreach (var wall in walls)
        if (SetComment(wall, ""checked by a script"")) count++;
    return count;
});
Ok($""Walls updated: {marked}"");

// A single action needs no braces.
Tx(""Comment the first wall"", () => SetComment(walls[0], ""first wall""));

// A method without parameters can be passed by name.
Tx(""Clear the comment of the last wall"", ClearLast);

// For another document, pass it first. Auto transaction covers only the active document,
// so this is needed even with the checkbox on. Linked documents are read-only.
// var other = App.OpenDocumentFile(@""C:\Projects\Other.rvt"");
// Tx(other, ""Mark walls"", () => SetComment(other.ToElements<Wall>().First(), ""checked""));
// other.Save();
// other.Close(false);

bool SetComment(Element element, string text)
{
    var p = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
    if (p == null || p.IsReadOnly) return false;
    p.Set(text);
    return true;
}

void ClearLast() => SetComment(walls[walls.Count - 1], """");
"
            },

            new Sample
            {
                Title = "5. Parameters of the selected elements",
                Code =
@"// S0 returns the first selected element, S1 the second, S2 the third.
// For an element by index use S(i), where i is the element's index starting from 0.
if (Selection.Count == 0) { Warn(""Nothing is selected in the model.""); return; }

// ""-"" is a parameter without a value; ""-empty-"" is an empty string.
foreach (var element in Selection)
    Dump(element);

// Other options:
// Dump(S0);         — one selected element
// Dump(S0, true);   — with the type's parameters
"
            },

            new Sample
            {
                Title = "6. Comparing the parameters of elements",
                Code =
@"// Select two to five elements in the model: only the parameters with different values are shown.
// Only the parameters visible in the Properties palette are compared.
// ""-none-"" (in orange): the element has no such parameter; ""-"": a parameter without a value; ""-empty-"": an empty string.
if (Selection.Count < 2) { Warn(""Select at least two elements in the model.""); return; }

Compare(Selection);

// Other options:
// Compare(S0, S1);            — two given elements
// Compare(S0, S1, S2);        — up to five elements, separated by commas
// Compare(Selection, true);   — with the types' parameters
"
            },

            new Sample
            {
                Title = "7. Showing geometry in the views",
                Code =
@"// Show(...) draws over the model without creating anything in it. Select a wall or any element.
// What is drawn stays until the next run; to clear it earlier, ClearShown() or the button on the pane.
if (Selection.Count == 0) { Warn(""Select an element.""); return; }

var element = S0;
Show(element);                                    // all the geometry: faces see-through, edges as lines
Show(element.get_BoundingBox(null), Orange);      // the bounding box
Show(element.Location, Red, ""location"");          // the location point or line

if (element is Wall wall)
{
    var side = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).FirstOrDefault();
    if (side != null) Show(wall.GetGeometryObjectFromReference(side), Green, ""exterior face"");
}

Show(XYZ.Zero, Purple, ""origin"");
"
            },

            new Sample
            {
                Title = "8. A complete IExternalCommand",
                Code =
@"// When the code declares a class, the add-in compiles the file as it is.
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

[Transaction(TransactionMode.Manual)]
public class MyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument.Document;

        Console.WriteLine(""The command runs the way Revit runs it."");
        Console.WriteLine(""Document: "" + doc.Title);

        using (var t = new Transaction(doc, ""Demo""))
        {
            t.Start();
            // ... changes to the model ...
            t.Commit();
        }

        return Result.Succeeded;
    }
}
"
            },

            new Sample
            {
                Title = "9. A class of your own with a Main method",
                Code =
@"using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// The method's parameters are filled by their type: UIApplication, UIDocument,
// Document, Application, ExternalCommandData.
public class Report
{
    public void Main(Document doc, UIApplication uiapp)
    {
        Console.WriteLine(""Document: "" + doc.Title);
        Console.WriteLine(""Version: "" + uiapp.Application.VersionNumber);

        var categories = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => e.Category != null)
            .GroupBy(e => e.Category.Name)
            .OrderByDescending(g => g.Count());

        foreach (var group in categories.Take(15))
            Console.WriteLine(group.Key.PadRight(40) + group.Count());
    }
}
"
            },

            new Sample
            {
                Title = "10. Referencing an external library with #r",
                Code =
@"// The #r directive adds a reference to an assembly: by name or by full path.
#r ""System.Xml""
#r ""System.Xml.Linq""

using System.Xml.Linq;

var xml = new XElement(""model"",
    new XElement(""title"", Doc.Title),
    new XElement(""walls"", All<Wall>().Count));

Print(xml.ToString());
"
            }
        };
    }
}
