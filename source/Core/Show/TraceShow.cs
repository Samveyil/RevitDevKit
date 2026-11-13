using Autodesk.Revit.DB;
using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Output;
using System;
using System.Collections;
using System.Linq;

namespace RevitDevKit.Core.Show
{
    // Show for add-ins, without a reference to DevKit: Debug.WriteLine(wall), Trace.WriteLine(solid, "Red: axis").
    // Debug and Trace hand the object itself to their listeners, DevKit's listener (ConsoleOutput) passes it here,
    // and geometry is drawn as Show(...) draws it in scripts. Anything else is written to the console as text.
    // Without DevKit the call only writes the object's ToString() to Visual Studio's Output; in Release builds
    // the compiler removes Debug calls altogether, Trace ones stay.
    internal static class TraceShow
    {
        // True when the value was geometry and is taken care of here: drawn, or a warning written instead.
        public static bool TryShow(object value, string category)
        {
            if (!IsGeometry(value, 0))
                return false;

            try
            {
                if (!ShownGeometry.IsApiThread)
                {
                    ConsoleOutput.Warning("Debug.WriteLine(" + value.GetType().Name + ") из фонового потока: " +
                                          "геометрию можно показать только из потока Revit API.");
                    return true;
                }

                ParseCategory(category, out var color, out var label);
                ShownGeometry.Show(DocumentOf(value), value, color, label);
                ShownGeometry.RequestRefresh();
            }
            catch (Exception ex)
            {
                // Debug.WriteLine must never throw into the add-in that called it.
                ConsoleOutput.Warning("Не удалось показать " + value.GetType().Name + ": " + ex.Message);
            }

            return true;
        }

        // What Show(...) draws. An ElementId is left out: Debug.WriteLine(id) is a common way to print the number.
        // A collection counts when it has items and all of them are geometry.
        private static bool IsGeometry(object value, int depth)
        {
            switch (value)
            {
                case null:
                case string _:
                case ElementId _:
                    return false;
                case GeometryObject _:
                case XYZ _:
                case BoundingBoxXYZ _:
                case Outline _:
                case LocationPoint _:
                case LocationCurve _:
                case Element _:
                case Reference _:
                    return true;
                case IEnumerable items when depth < 3:
                    var any = false;
                    foreach (var item in items)
                    {
                        if (!IsGeometry(item, depth + 1))
                            return false;
                        any = true;
                    }
                    return any;
                default:
                    return false;
            }
        }

        // "Red", "Red: axis", "#FF8000: axis" or just "axis".
        internal static void ParseCategory(string category, out Color color, out string label)
        {
            color = null;
            label = null;
            if (category.IsNullOrWhiteSpace())
                return;

            var text = category.Trim();
            var colon = text.IndexOf(':');
            color = ShownGeometry.ColorByName(colon >= 0 ? text.Substring(0, colon) : text);

            if (color == null)
                label = text;
            else if (colon >= 0 && text.Length > colon + 1)
                label = text.Substring(colon + 1).Trim();

            if (label.IsNullOrEmpty())
                label = null;
        }

        // An element belongs to its own document, which need not be the active one; the rest is drawn in the active one.
        private static Document DocumentOf(object value)
        {
            var element = value as Element
                          ?? (value is IEnumerable items && !(value is GeometryElement) ? items.OfType<Element>().FirstOrDefault() : null);
            if (element != null)
                return element.Document;

            try
            {
                return RevitAPI.Document;
            }
            catch
            {
                return null; // no document is open
            }
        }
    }
}
