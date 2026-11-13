using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Running;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitDevKit.Core.Show
{
    // One Show(...) of a script: what it drew, as segments and triangles in model coordinates.
    public sealed class ShownShape
    {
        public string Label { get; set; }
        public Color Color { get; set; }
        public string ColorName { get; set; }

        // Pairs of points: every two make a segment.
        public List<XYZ> Lines { get; } = new List<XYZ>();

        // Triples of points: every three make a triangle, drawn see-through.
        public List<XYZ> Triangles { get; } = new List<XYZ>();

        // "Solid (6 граней)": what it was, for the console.
        public string What { get; set; }
    }

    // What scripts showed on the views of a document, drawn by ShownGeometryServer over the model. Nothing goes into
    // the model. Cleared before each run from DevKit, so the views show the last run; ClearShown() clears it on demand.
    // Add-ins show geometry too, with Debug.WriteLine(geometry) (see TraceShow).
    public static class ShownGeometry
    {
        private static readonly object Sync = new object();
        private static readonly List<ShownShape> Shapes = new List<ShownShape>();
        private static int _nextColor;
        private static bool _dirty;
        private static int _refreshPosted;

        // The thread Revit runs the API on, taken at start-up. Geometry from another thread is not touched.
        public static int ApiThreadId { get; set; }

        public static bool IsApiThread => ApiThreadId == 0 || Thread.CurrentThread.ManagedThreadId == ApiThreadId;

        // The names a colour goes by in Debug.WriteLine(geometry, "Red: label"), as the ScriptBase properties are named.
        internal static readonly string[] PaletteNames = { "Orange", "Green", "Blue", "Red", "Purple", "Teal" };

        // Bumped on every change: the server rebuilds its buffers when it differs from the one they were built for.
        public static int Version { get; private set; }

        // The document the shapes belong to; views of other documents show nothing.
        public static Document Document { get; private set; }

        // A colour of its own for every Show: orange, green, blue, red, purple, teal, then again.
        internal static readonly Tuple<string, Color>[] Palette =
        {
            Tuple.Create("оранжевый", new Color(255, 128, 0)),
            Tuple.Create("зелёный", new Color(0, 170, 0)),
            Tuple.Create("синий", new Color(0, 110, 255)),
            Tuple.Create("красный", new Color(220, 0, 0)),
            Tuple.Create("фиолетовый", new Color(150, 0, 200)),
            Tuple.Create("бирюзовый", new Color(0, 160, 160)),
        };

        public static List<ShownShape> Snapshot()
        {
            lock (Sync) return Shapes.ToList();
        }

        public static ShownShape Add(Document document, object what, Color color, string label)
        {
            var shape = ShapeBuilder.Build(what, document);
            lock (Sync)
            {
                if (Document != null && document != null && !IsSame(Document, document))
                    Shapes.Clear();
                Document = document;

                if (color == null)
                {
                    var next = Palette[_nextColor++ % Palette.Length];
                    shape.Color = next.Item2;
                    shape.ColorName = next.Item1;
                }
                else
                {
                    shape.Color = color;
                    shape.ColorName = Palette.FirstOrDefault(p => Same(p.Item2, color))?.Item1 ?? "RGB " + color.Red + "," + color.Green + "," + color.Blue;
                }

                shape.Label = label;
                Shapes.Add(shape);
                Version++;
                _dirty = true;
            }

            return shape;
        }

        // Show(...) of a script, or Debug.WriteLine(geometry) of an add-in: draws and writes what it drew to the console.
        public static ShownShape Show(Document document, object what, Color color, string label)
        {
            var shape = Add(document, what, color, label);
            var line = "Показано: " + shape.What + ", " + shape.ColorName + (label.IsNullOrEmpty() ? "" : " — " + label);
            ConsoleOutput.WriteLine(line, shape.Lines.Count + shape.Triangles.Count == 0 ? LogLevel.Warning : LogLevel.Info);
            return shape;
        }

        // Out of a run from DevKit (an add-in's own command or event) nothing redraws the views after it: they redraw
        // when Revit is next free. One request covers any number of shapes added before it runs.
        public static void RequestRefresh()
        {
            if (Interlocked.Exchange(ref _refreshPosted, 1) == 1)
                return;

            RevitTaskRunner.Post(app =>
            {
                Interlocked.Exchange(ref _refreshPosted, 0);
                RefreshIfChanged(app.ActiveUIDocument);
            });
        }

        // "Red", "red", "красный", "#FF8000": a colour of the palette or any RGB; null for anything else.
        public static Color ColorByName(string name)
        {
            if (name.IsNullOrWhiteSpace())
                return null;

            name = name.Trim();
            for (var i = 0; i < Palette.Length; i++)
            {
                if (string.Equals(name, PaletteNames[i], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, Palette[i].Item1, StringComparison.OrdinalIgnoreCase))
                    return Palette[i].Item2;
            }

            if (name.Length == 7 && name[0] == '#' &&
                int.TryParse(name.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

            return null;
        }

        public static void Clear()
        {
            lock (Sync)
            {
                _nextColor = 0;
                if (Shapes.Count == 0) return;
                Shapes.Clear();
                Version++;
                _dirty = true;
            }
        }

        // In an API context, after a run or a clear: the open views redraw with what is shown now.
        public static void RefreshIfChanged(UIDocument document)
        {
            lock (Sync)
            {
                if (!_dirty) return;
                _dirty = false;
            }

            try { document?.UpdateAllOpenViews(); }
            catch (Exception ex) { ConsoleOutput.Debug("Виды не обновлены: " + ex.Message); }
        }

        private static bool IsSame(Document a, Document b)
        {
            try { return a.IsValidObject && b.IsValidObject && a.Equals(b); }
            catch { return false; }
        }

        private static bool Same(Color a, Color b) => a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;
    }

    // Revit geometry to segments and triangles: curves are tessellated, faces triangulated, by the API itself.
    public static class ShapeBuilder
    {
        // Half the size of the cross a point is drawn as, in feet: 15 cm each way.
        internal const double PointSize = 0.5;

        public static ShownShape Build(object what, Document document)
        {
            var shape = new ShownShape();
            var counts = new Counts();
            Add(shape, what, document, Transform.Identity, counts, 0);
            shape.What = Describe(what, counts);
            return shape;
        }

        private sealed class Counts
        {
            public int Faces, Curves, Points, Boxes, Items;
        }

        private static void Add(ShownShape shape, object what, Document document, Transform transform, Counts counts, int depth)
        {
            if (what == null || depth > 20) return;

            switch (what)
            {
                case XYZ point:
                    AddPoint(shape, transform.OfPoint(point));
                    counts.Points++;
                    return;
                case Point point:
                    AddPoint(shape, transform.OfPoint(point.Coord));
                    counts.Points++;
                    return;
                case Curve curve:
                    AddPolyline(shape, curve.Tessellate(), transform);
                    counts.Curves++;
                    return;
                case Edge edge:
                    AddPolyline(shape, edge.Tessellate(), transform);
                    counts.Curves++;
                    return;
                case PolyLine polyLine:
                    AddPolyline(shape, polyLine.GetCoordinates(), transform);
                    counts.Curves++;
                    return;
                case Face face:
                    AddFace(shape, face, transform);
                    counts.Faces++;
                    return;
                case Mesh mesh:
                    AddMesh(shape, mesh, transform);
                    counts.Faces++;
                    return;
                case Solid solid:
                    foreach (Face face in solid.Faces)
                    {
                        AddFace(shape, face, transform);
                        counts.Faces++;
                    }
                    return;
                case GeometryInstance instance:
                    Add(shape, instance.GetSymbolGeometry(), document, transform.Multiply(instance.Transform), counts, depth + 1);
                    return;
                case GeometryElement geometry:
                    foreach (var item in geometry) Add(shape, item, document, transform, counts, depth + 1);
                    return;
                case BoundingBoxXYZ box:
                    AddBox(shape, box.Min, box.Max, transform.Multiply(box.Transform ?? Transform.Identity));
                    counts.Boxes++;
                    return;
                case Outline outline:
                    AddBox(shape, outline.MinimumPoint, outline.MaximumPoint, transform);
                    counts.Boxes++;
                    return;
                case LocationPoint location:
                    Add(shape, location.Point, document, transform, counts, depth + 1);
                    return;
                case LocationCurve location:
                    Add(shape, location.Curve, document, transform, counts, depth + 1);
                    return;
                case Element element:
                    var geometryOfElement = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (geometryOfElement != null) Add(shape, geometryOfElement, document, transform, counts, depth + 1);
                    else Add(shape, element.Location, document, transform, counts, depth + 1);
                    return;
                case ElementId id:
                    Add(shape, id.ToElement(document), document, transform, counts, depth + 1);
                    return;
                case Reference reference:
                    Add(shape, reference.ToElement(document)?.GetGeometryObjectFromReference(reference), document, transform, counts, depth + 1);
                    return;
                case string _:
                    return;
                case IEnumerable items:
                    foreach (var item in items)
                    {
                        Add(shape, item, document, transform, counts, depth + 1);
                        counts.Items++;
                    }
                    return;
            }
        }

        private static void AddPoint(ShownShape shape, XYZ p)
        {
            foreach (var axis in new[] { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ })
            {
                shape.Lines.Add(p - axis * PointSize);
                shape.Lines.Add(p + axis * PointSize);
            }
        }

        private static void AddPolyline(ShownShape shape, IList<XYZ> points, Transform transform)
        {
            for (var i = 1; i < points.Count; i++)
            {
                shape.Lines.Add(transform.OfPoint(points[i - 1]));
                shape.Lines.Add(transform.OfPoint(points[i]));
            }
        }

        // A face: its triangles see-through, its edges as lines.
        private static void AddFace(ShownShape shape, Face face, Transform transform)
        {
            var mesh = face.Triangulate();
            if (mesh != null) AddMesh(shape, mesh, transform);

            foreach (EdgeArray loop in face.EdgeLoops)
            foreach (Edge edge in loop)
                AddPolyline(shape, edge.Tessellate(), transform);
        }

        private static void AddMesh(ShownShape shape, Mesh mesh, Transform transform)
        {
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                for (var j = 0; j < 3; j++)
                    shape.Triangles.Add(transform.OfPoint(triangle.get_Vertex(j)));
            }
        }

        private static void AddBox(ShownShape shape, XYZ min, XYZ max, Transform transform)
        {
            var corners = new[]
            {
                new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z), new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, min.Z),
                new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z), new XYZ(max.X, max.Y, max.Z), new XYZ(min.X, max.Y, max.Z),
            }.Select(transform.OfPoint).ToArray();

            foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7) })
            {
                shape.Lines.Add(corners[a]);
                shape.Lines.Add(corners[b]);
            }
        }

        // "Solid (6 граней)", "Line", "Wall (12 граней)", "3 объекта: 2 кривые, 1 точка".
        internal static string Describe(object what, int faces, int curves, int points, int boxes) =>
            Describe(what, new Counts { Faces = faces, Curves = curves, Points = points, Boxes = boxes });

        private static string Describe(object what, Counts counts)
        {
            var name = what == null ? "ничего" : what is Element element ? element.GetType().Name + " " + Id(element.Id) : what.GetType().Name;
            if (what is IEnumerable && !(what is string) && !(what is GeometryElement)) name = "список";

            var parts = new List<string>();
            if (counts.Faces > 0) parts.Add(Plural(counts.Faces, "грань", "грани", "граней"));
            if (counts.Curves > 0) parts.Add(Plural(counts.Curves, "кривая", "кривые", "кривых"));
            if (counts.Points > 0) parts.Add(Plural(counts.Points, "точка", "точки", "точек"));
            if (counts.Boxes > 0) parts.Add(Plural(counts.Boxes, "рамка", "рамки", "рамок"));

            if (parts.Count == 0) return name + " — нечего показать";
            return parts.Count == 1 && (counts.Curves == 1 || counts.Points == 1 || counts.Boxes == 1) && counts.Faces == 0 && !(what is IEnumerable)
                ? name
                : name + " (" + string.Join(", ", parts) + ")";
        }

        internal static string Plural(int n, string one, string few, string many)
        {
            var tens = n % 100;
            var ones = n % 10;
            var word = tens >= 11 && tens <= 14 ? many : ones == 1 ? one : ones >= 2 && ones <= 4 ? few : many;
            return n + " " + word;
        }

        private static string Id(ElementId id) => id.GetValue().ToString();
    }
}
