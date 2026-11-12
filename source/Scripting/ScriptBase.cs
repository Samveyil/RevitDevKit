using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using BIMPlugins.ExtStorage.Comparers;

namespace RevitDevKit.Scripting
{
    public abstract class ScriptBase
    {
        public UIApplication UIApp { get => RevitAPI.UIApplication; }
        public Autodesk.Revit.ApplicationServices.Application App { get => RevitAPI.Application; }
        public UIDocument UIDoc { get => RevitAPI.UIDocument; }
        public Document Doc { get => RevitAPI.Document; }
        public View ActiveView { get => RevitAPI.ActiveView; }
        public ExternalCommandData CommandData { get => RevitTaskRunner.CurrentCommandData; }

        public void Print(object value) => ConsoleOutput.WriteObject(value);
        public void Print(string format, params object[] args)
        {
            ConsoleOutput.WriteLine(args == null || args.Length == 0 ? format : string.Format(format, args));
        }

        public void Ok(string text) => ConsoleOutput.Success(text);
        public void Warn(string text) => ConsoleOutput.Warning(text);
        public void Fail(string text) => ConsoleOutput.Error(text);
        public void Clear() => ConsoleOutput.Clear();

        // Every column is as wide as its longest value, up to MaxTableWidth, so the separators line up
        // in the console's monospaced font. Line breaks inside a value would split the row, so they become spaces.
        public void Table<T>(IEnumerable<T> items, params Func<T, object>[] columns)
        {
            if (items == null)
            {
                Print("null");
                return;
            }

            if (columns == null || columns.Length == 0)
            {
                foreach (var item in items)
                    Print(item);
                return;
            }

            var rows = items.Select(item => columns.Select(c =>
            {
                try { return ConsoleOutput.Format(c(item)).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '); }
                catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
            }).ToList()).ToList();

            var widths = Enumerable.Range(0, columns.Length)
                .Select(i => Math.Min(MaxTableWidth, rows.Select(r => r[i].Length).DefaultIfEmpty(0).Max()))
                .ToList();

            foreach (var row in rows)
            {
                var cells = row.Select((cell, i) =>
                {
                    var text = Fit(cell, widths[i]);
                    return i < row.Count - 1 ? text.PadRight(widths[i]) : text;
                });
                ConsoleOutput.WriteLine(string.Join("  |  ", cells));
            }
        }

        public void Tx(string name, Action action)
        {
            if (action == null)
                throw new ArgumentNullException("action");

            var doc = Doc;
            if (doc == null)
                throw new InvalidOperationException("Нет активного документа — транзакцию открыть не в чем.");

            using (var t = new Transaction(doc, name))
            {
                t.Start();
                try
                {
                    action();

                    t.Commit();
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    throw;
                }
            }
        }

        public TResult Tx<TResult>(string name, Func<TResult> func)
        {
            var result = default(TResult);
            Tx(name, () => { result = func(); });
            return result;
        }

        public IList<Element> Instances(BuiltInCategory category) => Doc.ToElements(category);

        public IList<Element> Instances(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category);

        public IList<Element> Types(BuiltInCategory category) => Doc.ToElements(category, true);

        public IList<Element> Types(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category, true);

        public IList<T> All<T>() where T : Element => Doc.ToElements<T>();

        public IList<T> All<T>(ElementId viewId) where T : Element => Doc.ToElements<T>(viewId);

        public IList<T> All<T>(BuiltInCategory category) where T : Element => Doc.ToElements<T>(category);

        public IList<T> All<T>(ElementId viewId, BuiltInCategory category) where T : Element => Doc.ToElements<T>(viewId, category);

        public ICollection<Element> Selection { get => UIDoc.ToSelectedElements(); }

        public Element S0 { get => S(0); }

        public Element S1 { get => S(1); }

        public Element S2 { get => S(2); }

        public Element S(int index)
        {
            if (index < 0) return null;

            return Selection.Skip(index).FirstOrDefault();
        }

        public T S<T>(int index) where T : Element
        {
            return S(index) as T;
        }

        // Draws geometry over the model on the views, without creating anything: a point, a curve, an edge, a face,
        // a solid, a mesh, a bounding box or an outline, an element, its location, or a list of these. Every call has
        // a colour of its own unless one is given. What is shown stays until the next run or ClearShown().
        public void Show(object geometry) => Show(geometry, null, null);

        public void Show(object geometry, string label) => Show(geometry, null, label);

        public void Show(object geometry, Color color, string label = null)
        {
            var shape = ShownGeometry.Add(Doc, geometry, color, label);
            var line = "Показано: " + shape.What + ", " + shape.ColorName + (label.IsNullOrEmpty() ? "" : " — " + label);
            ConsoleOutput.WriteLine(line, shape.Lines.Count + shape.Triangles.Count == 0 ? LogLevel.Warning : LogLevel.Info);
        }

        // Clears what Show(...) drew; it is also cleared before each run.
        public void ClearShown() => ShownGeometry.Clear();

        // Colours for Show(..., colour).
        public Color Orange => ShownGeometry.Palette[0].Item2;
        public Color Green => ShownGeometry.Palette[1].Item2;
        public Color Blue => ShownGeometry.Palette[2].Item2;
        public Color Red => ShownGeometry.Palette[3].Item2;
        public Color Purple => ShownGeometry.Palette[4].Item2;
        public Color Teal => ShownGeometry.Palette[5].Item2;

        public void Dump(Element element, bool useType=false)
        {
            if (element == null)
            {
                Print("null");
                return;
            }

            ConsoleOutput.Banner(element.Id + "  " + (element.Name ?? "<без имени>"));
            ConsoleOutput.WriteLine("Класс:     " + element.GetType().FullName);
            ConsoleOutput.WriteLine("Категория: " + (element.Category != null ? element.Category.Name : "—"));

            foreach (var p in ReadParameters(element, useType).OrderBy(p => p.Definition.Name.ToUpperInvariant(), new NaturalComparer()))
                ConsoleOutput.WriteLine("  " + p.Definition.Name.PadRight(NameWidth) + " = " + ReadValue(p));
        }

        // Prints only the parameters whose values differ; "—" means the element has no such parameter.
        // Only parameters shown in the Properties palette (GetOrderedParameters).
        public void Compare(Element first, Element second, bool useType = false) =>
            Compare(new[] { first, second }, useType);

        public void Compare(params Element[] elements) => Compare(elements, false);

        // Up to MaxCompared elements: wider tables no longer fit a docked console pane.
        public void Compare(IEnumerable<Element> elements, bool useType = false)
        {
            var items = (elements ?? Enumerable.Empty<Element>()).ToList();

            if (items.Count < 2 || items.Any(e => e == null))
            {
                Warn("Для сравнения нужны минимум два элемента, и ни один не должен быть null.");
                return;
            }

            if (items.Count > MaxCompared)
            {
                Warn("Сравниваются первые " + MaxCompared + " из " + items.Count + " элементов.");
                items = items.Take(MaxCompared).ToList();
            }

            var maps = items.Select(e => ByKey(ReadParameters(e, useType, visibleOnly: true)))
                .ToList();

            var rows = maps.SelectMany(m => m.Keys).Distinct()
                .Select(key => maps.Select(m => m.TryGetValue(key, out var p) ? p : null).ToList())
                .Where(ps => ps.Any(p => p == null) || ps.Skip(1).Any(p => !SameValue(ps[0], p)))
                .Select(ps => new
                {
                    Name = ps.First(p => p != null).Definition.Name,
                    Values = ps.Select(p => p == null ? null : ReadValue(p)).ToList()
                })
                .OrderBy(r => r.Name.ToUpperInvariant(), new NaturalComparer())
                .ToList();

            var titles = items.Select(e => e.Id + " " + (e.Name ?? "<без имени>")).ToList();

            ConsoleOutput.Banner("Сравнение: " + string.Join("  ↔  ", titles));

            if (rows.Count == 0)
            {
                Ok("Все параметры совпадают.");
                return;
            }

            // Every column is as wide as its longest value or title, but not wider than MaxValueWidth.
            var widths = Enumerable.Range(0, items.Count)
                .Select(i => Math.Min(MaxValueWidth, rows.Select(r => (r.Values[i] ?? Missing).Length).Append(titles[i].Length).Max()))
                .ToList();

            // Written cell by cell so a missing parameter (null) can be coloured on its own.
            void Row(string name, IList<string> cells)
            {
                ConsoleOutput.Write("  " + name.PadRight(NameWidth));

                for (var i = 0; i < cells.Count; i++)
                {
                    var text = Fit(cells[i] ?? Missing, widths[i]);
                    if (i < cells.Count - 1) text = text.PadRight(widths[i]);

                    ConsoleOutput.Write(" | ");
                    ConsoleOutput.Write(text, cells[i] == null ? LogLevel.Warning : LogLevel.Info);
                }

                ConsoleOutput.WriteLine();
            }

            Row("Параметр", titles);
            ConsoleOutput.WriteLine("  " + new string('-', NameWidth) + string.Concat(widths.Select(w => "-+-" + new string('-', w))));

            foreach (var r in rows)
                Row(r.Name, r.Values);

            ConsoleOutput.WriteLine("Различий: " + rows.Count, LogLevel.Warning);
        }

        private const int NameWidth = 38;
        private const int MaxValueWidth = 20;
        private const int MaxTableWidth = 50;
        private const int MaxCompared = 5;
        private const string Missing = "-нет-";

        private static List<Parameter> ReadParameters(Element element, bool useType, bool visibleOnly = false)
        {
            IEnumerable<Parameter> Of(Element e) =>
                visibleOnly ? e.GetOrderedParameters() : e.Parameters.Cast<Parameter>();

            var parameters = Of(element).ToList();

            if (useType)
            {
                var type = element.ToElementType();
                if (type != null)
                    parameters.AddRange(Of(type));
            }

            return parameters.Where(p => p.Definition != null).ToList();
        }

        private static string ReadValue(Parameter p)
        {
            try
            {
                if (!p.HasValue) return "-";

                var text = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                return text.IsNullOrEmpty() ? "-пусто-" : text;
            }
            catch
            {
                return "<ошибка чтения>";
            }
        }

        // Names are not unique (e.g. a shared and a project parameter may share one), so match
        // built-in parameters by id and shared ones by GUID. With useType the instance wins.
        private static Dictionary<string, Parameter> ByKey(IEnumerable<Parameter> parameters)
        {
            var result = new Dictionary<string, Parameter>();

            foreach (var p in parameters)
            {
                string key;
                if (p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID)
                    key = "bip:" + (int)d.BuiltInParameter;
                else if (p.IsShared)
                    key = "guid:" + p.GUID;
                else
                    key = "name:" + p.Definition.Name;

                if (!result.ContainsKey(key))
                    result.Add(key, p);
            }

            return result;
        }

        // Compares stored values, not display strings, so rounding in AsValueString hides nothing.
        private static bool SameValue(Parameter a, Parameter b)
        {
            try
            {
                var blankA = IsBlank(a);
                var blankB = IsBlank(b);
                if (blankA || blankB) return blankA && blankB;

                if (a.StorageType != b.StorageType) return ReadValue(a) == ReadValue(b);

                var valueA = a.GetValue();
                var valueB = b.GetValue();
                return valueA is double x && valueB is double y ? Math.Abs(x - y) < 1e-9 : Equals(valueA, valueB);
            }
            catch
            {
                return ReadValue(a) == ReadValue(b);
            }
        }

        private static bool IsBlank(Parameter p) => !p.HasValue || (p.StorageType == StorageType.String && p.AsString().IsNullOrEmpty());

        private static string Fit(string text, int width) =>
            text.Length <= width ? text : text.Substring(0, width - 1) + "…";
    }
}
