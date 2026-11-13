using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
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
    /// Основа каждого скрипта: его код выполняется внутри наследника этого класса, поэтому всё ниже доступно
    /// без префикса: Doc, Print(...), All&lt;Wall&gt;() и так далее.
    /// </summary>
    public abstract class ScriptBase
    {
        /// <summary>Приложение Revit с интерфейсом: открытые документы, активный документ, лента.</summary>
        public UIApplication UIApp { get => RevitAPI.UIApplication; }

        /// <summary>Приложение Revit без интерфейса: версия, язык, единицы, открытие и создание документов.</summary>
        public Autodesk.Revit.ApplicationServices.Application App { get => RevitAPI.Application; }

        /// <summary>Активный документ с интерфейсом: выделение, показ элементов, активный вид.</summary>
        public UIDocument UIDoc { get => RevitAPI.UIDocument; }

        /// <summary>Активный документ: модель, из которой читают элементы и в которой меняют их в транзакции (Tx).</summary>
        public Document Doc { get => RevitAPI.Document; }

        /// <summary>Вид, открытый сейчас в Revit.</summary>
        public View ActiveView { get => RevitAPI.ActiveView; }

        /// <summary>
        /// Данные внешней команды, как в IExternalCommand.Execute. Есть, когда запуск идёт через команду Revit
        /// (так запускаются команды из DLL и Repeat Last); иначе null.
        /// </summary>
        public ExternalCommandData CommandData { get => RevitTaskRunner.CurrentCommandData; }

        /// <summary>
        /// Пишет значение в консоль. Список выводится как [a, b, c] (первые 200), null — как "null".
        /// </summary>
        /// <param name="value">Что вывести: строка, число, элемент, список и так далее.</param>
        public void Print(object value) => ConsoleOutput.WriteObject(value);

        /// <summary>Пишет в консоль строку по шаблону, как string.Format: Print("Стен: {0}", walls.Count).</summary>
        /// <param name="format">Шаблон с местами {0}, {1}...</param>
        /// <param name="args">Значения для мест в шаблоне.</param>
        public void Print(string format, params object[] args)
        {
            ConsoleOutput.WriteLine(args == null || args.Length == 0 ? format : string.Format(format, args));
        }

        /// <summary>Пишет в консоль зелёную строку: всё получилось.</summary>
        /// <param name="text">Текст сообщения.</param>
        public void Ok(string text) => ConsoleOutput.Success(text);

        /// <summary>Пишет в консоль жёлтую строку: предупреждение.</summary>
        /// <param name="text">Текст предупреждения.</param>
        public void Warn(string text) => ConsoleOutput.Warning(text);

        /// <summary>Пишет в консоль красную строку: ошибка. Скрипт при этом продолжает работу.</summary>
        /// <param name="text">Текст ошибки.</param>
        public void Fail(string text) => ConsoleOutput.Error(text);

        /// <summary>Очищает консоль.</summary>
        public void Clear() => ConsoleOutput.Clear();

        /// <summary>
        /// Выводит в консоль таблицу: строка на каждый элемент, столбец на каждую функцию.
        /// Например: Table(walls, w =&gt; w.Id, w =&gt; w.Name).
        /// </summary>
        /// <param name="items">Строки таблицы.</param>
        /// <param name="columns">Что показать в столбцах; без них каждый элемент выводится как Print.</param>
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

        /// <summary>
        /// Выполняет изменения модели в транзакции: Tx("Перенос", () =&gt; { ... }). При исключении всё
        /// откатывается. Не нужна, если включена «Автотранзакция».
        /// </summary>
        /// <param name="name">Имя транзакции, оно будет в списке отмены Revit.</param>
        /// <param name="action">Код, который меняет модель.</param>
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

        /// <summary>
        /// Выполняет изменения модели в транзакции и возвращает результат:
        /// var wall = Tx("Стена", () =&gt; Wall.Create(...)). При исключении всё откатывается.
        /// </summary>
        /// <param name="name">Имя транзакции, оно будет в списке отмены Revit.</param>
        /// <param name="func">Код, который меняет модель и возвращает результат.</param>
        public TResult Tx<TResult>(string name, Func<TResult> func)
        {
            var result = default(TResult);
            Tx(name, () => { result = func(); });
            return result;
        }

        /// <summary>Все экземпляры категории в документе, без типов: Instances(BuiltInCategory.OST_Walls).</summary>
        /// <param name="category">Категория, например BuiltInCategory.OST_Doors.</param>
        public IList<Element> Instances(BuiltInCategory category) => Doc.ToElements(category);

        /// <summary>Экземпляры категории, видимые на виде.</summary>
        /// <param name="viewId">Id вида, например ActiveView.Id.</param>
        /// <param name="category">Категория, например BuiltInCategory.OST_Doors.</param>
        public IList<Element> Instances(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category);

        /// <summary>Все типы категории в документе: Types(BuiltInCategory.OST_Walls) вернёт типы стен.</summary>
        /// <param name="category">Категория, например BuiltInCategory.OST_Walls.</param>
        public IList<Element> Types(BuiltInCategory category) => Doc.ToElements(category, true);

        /// <summary>Типы категории, отобранные по виду.</summary>
        /// <param name="viewId">Id вида, например ActiveView.Id.</param>
        /// <param name="category">Категория, например BuiltInCategory.OST_Walls.</param>
        public IList<Element> Types(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category, true);

        /// <summary>Все элементы класса T в документе: All&lt;Wall&gt;(), All&lt;FamilyInstance&gt;().</summary>
        public IList<T> All<T>() where T : Element => Doc.ToElements<T>();

        /// <summary>Элементы класса T, видимые на виде: All&lt;Wall&gt;(ActiveView.Id).</summary>
        /// <param name="viewId">Id вида, например ActiveView.Id.</param>
        public IList<T> All<T>(ElementId viewId) where T : Element => Doc.ToElements<T>(viewId);

        /// <summary>Элементы класса T из категории: All&lt;FamilyInstance&gt;(BuiltInCategory.OST_Doors).</summary>
        /// <param name="category">Категория, например BuiltInCategory.OST_Doors.</param>
        public IList<T> All<T>(BuiltInCategory category) where T : Element => Doc.ToElements<T>(category);

        /// <summary>Элементы класса T из категории, видимые на виде.</summary>
        /// <param name="viewId">Id вида, например ActiveView.Id.</param>
        /// <param name="category">Категория, например BuiltInCategory.OST_Doors.</param>
        public IList<T> All<T>(ElementId viewId, BuiltInCategory category) where T : Element => Doc.ToElements<T>(viewId, category);

        /// <summary>Элементы, выделенные сейчас в Revit.</summary>
        public ICollection<Element> Selection { get => UIDoc.ToSelectedElements(); }

        /// <summary>Первый выделенный элемент, или null, если ничего не выделено.</summary>
        public Element S0 { get => S(0); }

        /// <summary>Второй выделенный элемент, или null.</summary>
        public Element S1 { get => S(1); }

        /// <summary>Третий выделенный элемент, или null.</summary>
        public Element S2 { get => S(2); }

        /// <summary>Выделенный элемент по номеру, с нуля; null, если столько не выделено.</summary>
        /// <param name="index">Номер в выделении: 0 — первый.</param>
        public Element S(int index)
        {
            if (index < 0) return null;

            return Selection.Skip(index).FirstOrDefault();
        }

        /// <summary>Выделенный элемент по номеру как T: S&lt;Wall&gt;(0); null, если это не T.</summary>
        /// <param name="index">Номер в выделении: 0 — первый.</param>
        public T S<T>(int index) where T : Element
        {
            return S(index) as T;
        }

        /// <summary>
        /// Рисует геометрию поверх модели на видах, ничего не создавая в ней. Каждый вызов получает свой цвет.
        /// Нарисованное остаётся до следующего запуска или ClearShown().
        /// </summary>
        /// <param name="geometry">
        /// Точка, кривая, ребро, грань, тело, сетка, BoundingBoxXYZ или Outline, элемент или его Id, Reference,
        /// LocationPoint или LocationCurve, или список из них.
        /// </param>
        public void Show(object geometry) => Show(geometry, null, null);

        /// <summary>Рисует геометрию поверх модели и подписывает её в консоли.</summary>
        /// <param name="geometry">
        /// Точка, кривая, ребро, грань, тело, сетка, BoundingBoxXYZ или Outline, элемент или его Id, Reference,
        /// LocationPoint или LocationCurve, или список из них.
        /// </param>
        /// <param name="label">Подпись, которая выводится в консоли рядом с цветом.</param>
        public void Show(object geometry, string label) => Show(geometry, null, label);

        /// <summary>Рисует геометрию поверх модели заданным цветом: Show(wall, Red, "стена").</summary>
        /// <param name="geometry">
        /// Точка, кривая, ребро, грань, тело, сетка, BoundingBoxXYZ или Outline, элемент или его Id, Reference,
        /// LocationPoint или LocationCurve, или список из них.
        /// </param>
        /// <param name="color">Цвет: Orange, Green, Blue, Red, Purple, Teal или new Color(r, g, b).</param>
        /// <param name="label">Подпись, которая выводится в консоли рядом с цветом.</param>
        public void Show(object geometry, Color color, string label = null)
        {
            var shape = ShownGeometry.Add(Doc, geometry, color, label);
            var line = "Показано: " + shape.What + ", " + shape.ColorName + (label.IsNullOrEmpty() ? "" : " — " + label);
            ConsoleOutput.WriteLine(line, shape.Lines.Count + shape.Triangles.Count == 0 ? LogLevel.Warning : LogLevel.Info);
        }

        /// <summary>Стирает всё, что нарисовал Show(...). Перед каждым запуском это происходит и само.</summary>
        public void ClearShown() => ShownGeometry.Clear();

        /// <summary>Оранжевый для Show(..., Orange).</summary>
        public Color Orange => ShownGeometry.Palette[0].Item2;

        /// <summary>Зелёный для Show(..., Green).</summary>
        public Color Green => ShownGeometry.Palette[1].Item2;

        /// <summary>Синий для Show(..., Blue).</summary>
        public Color Blue => ShownGeometry.Palette[2].Item2;

        /// <summary>Красный для Show(..., Red).</summary>
        public Color Red => ShownGeometry.Palette[3].Item2;

        /// <summary>Фиолетовый для Show(..., Purple).</summary>
        public Color Purple => ShownGeometry.Palette[4].Item2;

        /// <summary>Бирюзовый для Show(..., Teal).</summary>
        public Color Teal => ShownGeometry.Palette[5].Item2;

        /// <summary>Выводит в консоль класс, категорию и все параметры элемента со значениями, по алфавиту.</summary>
        /// <param name="element">Элемент, например S0.</param>
        /// <param name="useType">true — добавить параметры его типа.</param>
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

            foreach (var p in ReadParameters(element, useType).OrderBy(p => p.Definition.Name))
                ConsoleOutput.WriteLine("  " + p.Definition.Name.PadRight(NameWidth) + " = " + ReadValue(p));
        }

        /// <summary>
        /// Выводит в консоль таблицу параметров, которые у двух элементов различаются. «-нет-» — у элемента
        /// такого параметра нет. Сравниваются параметры из палитры свойств.
        /// </summary>
        /// <param name="first">Первый элемент, например S0.</param>
        /// <param name="second">Второй элемент, например S1.</param>
        /// <param name="useType">true — сравнить и параметры их типов.</param>
        public void Compare(Element first, Element second, bool useType = false) =>
            Compare(new[] { first, second }, useType);

        /// <summary>Выводит в консоль параметры, которые у элементов различаются: Compare(S0, S1, S2). До пяти элементов.</summary>
        /// <param name="elements">Элементы для сравнения.</param>
        public void Compare(params Element[] elements) => Compare(elements, false);

        /// <summary>Выводит в консоль параметры, которые у элементов различаются: Compare(Selection). До пяти элементов.</summary>
        /// <param name="elements">Элементы для сравнения.</param>
        /// <param name="useType">true — сравнить и параметры их типов.</param>
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
                .OrderBy(r => r.Name)
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
