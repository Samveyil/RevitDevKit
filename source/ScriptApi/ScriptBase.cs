using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Comparers;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Running;
using RevitDevKit.Core.Show;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDevKit.ScriptApi
{
    /// <summary>
    /// The base class of every script. The script's code runs inside a subclass of it, so its members are
    /// available without a prefix: Doc, Print(...), All&lt;Wall&gt;() and so on.
    /// </summary>
    public abstract class ScriptBase
    {
        /// <inheritdoc cref="RevitAPI.UIApplication"/>
        public UIApplication UIApp { get => RevitAPI.UIApplication; }

        /// <inheritdoc cref="RevitAPI.Application"/>
        public Autodesk.Revit.ApplicationServices.Application App { get => RevitAPI.Application; }

        /// <inheritdoc cref="RevitAPI.UIDocument"/>
        public UIDocument UIDoc { get => RevitAPI.UIDocument; }

        /// <inheritdoc cref="RevitAPI.Document"/>
        public Document Doc { get => RevitAPI.Document; }

        /// <inheritdoc cref="RevitAPI.ActiveView"/>
        public View ActiveView { get => RevitAPI.ActiveView; }

        /// <summary>The data of the external command the script runs in, as passed to IExternalCommand.Execute.</summary>
        /// <value>
        /// Available when the run goes through a Revit command, as DLL commands and Repeat Last do;
        /// <see langword="null"/> otherwise.
        /// </value>
        public ExternalCommandData CommandData { get => RevitTaskRunner.CurrentCommandData; }

        /// <summary>Writes a value to the console.</summary>
        /// <param name="value">The value to write. A collection is written as [a, b, c] (the first 200 items).</param>
        public void Print(object value) => ConsoleOutput.WriteObject(value);

        /// <summary>Writes a formatted line to the console, as string.Format does.</summary>
        /// <param name="format">A composite format string, such as "Walls: {0}".</param>
        /// <param name="args">The objects to format.</param>
        public void Print(string format, params object[] args)
        {
            ConsoleOutput.WriteLine(args == null || args.Length == 0 ? format : string.Format(format, args));
        }

        /// <summary>Writes a success message to the console, in green.</summary>
        /// <param name="text">The message.</param>
        public void Ok(string text) => ConsoleOutput.Success(text);

        /// <summary>Writes a warning to the console, in yellow.</summary>
        /// <param name="text">The warning.</param>
        public void Warn(string text) => ConsoleOutput.Warning(text);

        /// <summary>Writes an error message to the console, in red.</summary>
        /// <param name="text">The error message.</param>
        /// <remarks>The script is not stopped.</remarks>
        public void Fail(string text) => ConsoleOutput.Error(text);

        /// <summary>Clears the console.</summary>
        public void Clear() => ConsoleOutput.Clear();

        /// <summary>Writes a table to the console: a row for each item, a column for each selector.</summary>
        /// <typeparam name="T">The type of the items.</typeparam>
        /// <param name="items">The items, one per row.</param>
        /// <param name="columns">
        /// The values to show for each item, such as w =&gt; w.Id, w =&gt; w.Name.
        /// Without them each item is written as by Print.
        /// </param>
        public void Table<T>(IEnumerable<T> items, params Func<T, object>[] columns)
        {
            // Every column is as wide as its longest value, up to MaxTableWidth, so the separators line up
            // in the console's monospaced font. Line breaks inside a value would split the row, so they become spaces.
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

        /// <summary>Runs an action that modifies the model in a transaction of the active document.</summary>
        /// <param name="name">The name of the transaction, as it appears in the Undo list.</param>
        /// <param name="action">The action that modifies the model.</param>
        /// <remarks>
        /// The transaction is committed when the action completes and rolled back if it throws.
        /// Not needed when Auto transaction is on.
        /// </remarks>
        /// <exception cref="ArgumentNullException">action is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">There is no active document.</exception>
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

        /// <summary>Runs a function that modifies the model in a transaction of the active document and returns its result.</summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="name">The name of the transaction, as it appears in the Undo list.</param>
        /// <param name="func">The function that modifies the model, such as () =&gt; Wall.Create(...).</param>
        /// <returns>The result of the function.</returns>
        /// <remarks>The transaction is committed when the function completes and rolled back if it throws.</remarks>
        public TResult Tx<TResult>(string name, Func<TResult> func)
        {
            var result = default(TResult);
            Tx(name, () => { result = func(); });
            return result;
        }

        /// <summary>Returns the element instances of a category in the active document.</summary>
        /// <param name="category">The category, such as BuiltInCategory.OST_Walls.</param>
        /// <returns>The instances; element types are not included.</returns>
        public IList<Element> Instances(BuiltInCategory category) => Doc.ToElements(category);

        /// <summary>Returns the element instances of a category visible in a view.</summary>
        /// <param name="viewId">The id of the view, such as ActiveView.Id.</param>
        /// <param name="category">The category, such as BuiltInCategory.OST_Walls.</param>
        /// <returns>The instances; element types are not included.</returns>
        public IList<Element> Instances(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category);

        /// <summary>Returns the element types of a category in the active document.</summary>
        /// <param name="category">The category, such as BuiltInCategory.OST_Walls.</param>
        /// <returns>The element types, such as the wall types for OST_Walls.</returns>
        public IList<Element> Types(BuiltInCategory category) => Doc.ToElements(category, true);

        /// <summary>Returns the element types of a category, collected in a view.</summary>
        /// <param name="viewId">The id of the view, such as ActiveView.Id.</param>
        /// <param name="category">The category, such as BuiltInCategory.OST_Walls.</param>
        /// <returns>The element types.</returns>
        public IList<Element> Types(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category, true);

        /// <summary>Returns all elements of a class in the active document.</summary>
        /// <typeparam name="T">The element class, such as Wall or FamilyInstance.</typeparam>
        /// <returns>The elements of the class.</returns>
        public IList<T> All<T>() where T : Element => Doc.ToElements<T>();

        /// <summary>Returns the elements of a class visible in a view.</summary>
        /// <typeparam name="T">The element class, such as Wall or FamilyInstance.</typeparam>
        /// <param name="viewId">The id of the view, such as ActiveView.Id.</param>
        /// <returns>The elements of the class.</returns>
        public IList<T> All<T>(ElementId viewId) where T : Element => Doc.ToElements<T>(viewId);

        /// <summary>Returns the elements of a class in a category.</summary>
        /// <typeparam name="T">The element class, such as FamilyInstance.</typeparam>
        /// <param name="category">The category, such as BuiltInCategory.OST_Doors.</param>
        /// <returns>The elements of the class in the category.</returns>
        public IList<T> All<T>(BuiltInCategory category) where T : Element => Doc.ToElements<T>(category);

        /// <summary>Returns the elements of a class in a category visible in a view.</summary>
        /// <typeparam name="T">The element class, such as FamilyInstance.</typeparam>
        /// <param name="viewId">The id of the view, such as ActiveView.Id.</param>
        /// <param name="category">The category, such as BuiltInCategory.OST_Doors.</param>
        /// <returns>The elements of the class in the category.</returns>
        public IList<T> All<T>(ElementId viewId, BuiltInCategory category) where T : Element => Doc.ToElements<T>(viewId, category);

        /// <summary>The elements currently selected in Revit.</summary>
        public ICollection<Element> Selection { get => UIDoc.ToSelectedElements(); }

        /// <summary>The first selected element.</summary>
        /// <value><see langword="null"/> if nothing is selected.</value>
        public Element S0 { get => S(0); }

        /// <summary>The second selected element.</summary>
        /// <value><see langword="null"/> if fewer than two elements are selected.</value>
        public Element S1 { get => S(1); }

        /// <summary>The third selected element.</summary>
        /// <value><see langword="null"/> if fewer than three elements are selected.</value>
        public Element S2 { get => S(2); }

        /// <summary>Returns a selected element by its index in the selection.</summary>
        /// <param name="index">The zero-based index in the selection.</param>
        /// <returns>The element, or <see langword="null"/> if fewer elements are selected.</returns>
        public Element S(int index)
        {
            if (index < 0) return null;

            return Selection.Skip(index).FirstOrDefault();
        }

        /// <summary>Returns a selected element of a class by its index in the selection.</summary>
        /// <typeparam name="T">The element class, such as Wall.</typeparam>
        /// <param name="index">The zero-based index in the selection.</param>
        /// <returns>The element, or <see langword="null"/> if fewer elements are selected or the element is not a T.</returns>
        public T S<T>(int index) where T : Element
        {
            return S(index) as T;
        }

        /// <summary>Draws geometry over the model in the views, without creating anything in the document.</summary>
        /// <param name="geometry">
        /// A point, curve, edge, face, solid, mesh, BoundingBoxXYZ or Outline, an element or its id, a Reference,
        /// a LocationPoint or LocationCurve, or a collection of these.
        /// </param>
        /// <remarks>
        /// Each call gets a color of its own. What is drawn stays until the next run or ClearShown().
        /// </remarks>
        public void Show(object geometry) => Show(geometry, null, null);

        /// <summary>Draws geometry over the model in the views and labels it in the console.</summary>
        /// <param name="geometry">
        /// A point, curve, edge, face, solid, mesh, BoundingBoxXYZ or Outline, an element or its id, a Reference,
        /// a LocationPoint or LocationCurve, or a collection of these.
        /// </param>
        /// <param name="label">The label written to the console next to the color.</param>
        /// <remarks>
        /// Each call gets a color of its own. What is drawn stays until the next run or ClearShown().
        /// </remarks>
        public void Show(object geometry, string label) => Show(geometry, null, label);

        /// <summary>Draws geometry over the model in the views in the given color.</summary>
        /// <param name="geometry">
        /// A point, curve, edge, face, solid, mesh, BoundingBoxXYZ or Outline, an element or its id, a Reference,
        /// a LocationPoint or LocationCurve, or a collection of these.
        /// </param>
        /// <param name="color">The color: Orange, Green, Blue, Red, Purple, Teal, or new Color(r, g, b).</param>
        /// <param name="label">The label written to the console next to the color.</param>
        /// <remarks>What is drawn stays until the next run or ClearShown().</remarks>
        public void Show(object geometry, Color color, string label = null) => ShownGeometry.Show(Doc, geometry, color, label);

        /// <summary>Clears all geometry drawn by Show.</summary>
        /// <remarks>It is also cleared before each run.</remarks>
        public void ClearShown() => ShownGeometry.Clear();

        /// <summary>Orange, for Show(geometry, Orange).</summary>
        public Color Orange => ShownGeometry.Palette[0].Item2;

        /// <summary>Green, for Show(geometry, Green).</summary>
        public Color Green => ShownGeometry.Palette[1].Item2;

        /// <summary>Blue, for Show(geometry, Blue).</summary>
        public Color Blue => ShownGeometry.Palette[2].Item2;

        /// <summary>Red, for Show(geometry, Red).</summary>
        public Color Red => ShownGeometry.Palette[3].Item2;

        /// <summary>Purple, for Show(geometry, Purple).</summary>
        public Color Purple => ShownGeometry.Palette[4].Item2;

        /// <summary>Teal, for Show(geometry, Teal).</summary>
        public Color Teal => ShownGeometry.Palette[5].Item2;

        /// <summary>Writes the class, the category and all parameters of an element with their values to the console.</summary>
        /// <param name="element">The element, such as S0.</param>
        /// <param name="useType">True to include the parameters of the element's type.</param>
        /// <remarks>The parameters are sorted by name.</remarks>
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

            foreach (var p in ReadParameters(element, useType).OrderBy(p => p.Definition.Name, NaturalComparer.CurrentCultureIgnoreCase))
                ConsoleOutput.WriteLine("  " + p.Definition.Name.PadRight(NameWidth) + " = " + ReadValue(p));
        }

        /// <summary>Writes a table of the parameters whose values differ between two elements to the console.</summary>
        /// <param name="first">The first element, such as S0.</param>
        /// <param name="second">The second element, such as S1.</param>
        /// <param name="useType">True to compare the parameters of the elements' types as well.</param>
        /// <remarks>
        /// Only the parameters shown in the Properties palette are compared.
        /// "-нет-" marks a parameter the element does not have.
        /// </remarks>
        public void Compare(Element first, Element second, bool useType = false) =>
            Compare(new[] { first, second }, useType);

        /// <summary>Writes a table of the parameters whose values differ between elements to the console.</summary>
        /// <param name="elements">The elements to compare, such as S0, S1, S2.</param>
        /// <remarks>Up to five elements are compared. Only the parameters shown in the Properties palette are compared.</remarks>
        public void Compare(params Element[] elements) => Compare(elements, false);

        /// <summary>Writes a table of the parameters whose values differ between elements to the console.</summary>
        /// <param name="elements">The elements to compare, such as Selection.</param>
        /// <param name="useType">True to compare the parameters of the elements' types as well.</param>
        /// <remarks>Up to five elements are compared. Only the parameters shown in the Properties palette are compared.</remarks>
        public void Compare(IEnumerable<Element> elements, bool useType = false)
        {
            // Up to MaxCompared elements: wider tables no longer fit a docked console pane.
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
                .OrderBy(r => r.Name, NaturalComparer.CurrentCultureIgnoreCase)
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
