namespace RevitDevKit.ScriptApi
{
    // The examples in Russian; the English ones are in Samples.cs.
    public static partial class Samples
    {
        private const string RussianStarter =
@"// Короткий фрагмент: плагин сам обернёт его в класс.
// Доступны Doc, UIDoc, UIApp, App, ActiveView, Selection, S0, S1, S2.
// Также Print() / Tx() / S() / All() / Instances() / Types() / Table() / Dump() / Compare() / Show().
// Запуск — F5 или Ctrl+Enter.

Print(""Документ: "" + Doc.Title);
Print(""Путь: "" + (Doc.PathName == """" ? ""<не сохранён>"" : Doc.PathName));

var walls = All<Wall>();
Print($""Стен в модели: {walls.Count}"");
";

        private static readonly Sample[] RussianSamples =
        {
            new Sample
            {
                Title = "1. Привет, Revit",
                Code =
@"Print(""Revit "" + App.VersionNumber + "" сборка "" + App.VersionBuild);
Print(""Документ: "" + Doc.Title);
Print(""Пользователь: "" + App.Username);
"
            },

            new Sample
            {
                Title = "2. Console.WriteLine и уровни вывода",
                Code =
@"// Обычный Console.WriteLine тоже попадает в консоль плагина —
// поэтому чужие библиотеки логируются сюда же без переделок.
Console.WriteLine(""Это обычный Console.WriteLine"");
System.Diagnostics.Debug.WriteLine(""Это Debug.WriteLine (серый)"");

Ok(""Успех — зелёный"");
Warn(""Предупреждение — жёлтое"");
Fail(""Ошибка — красная"");

for (int i = 1; i <= 5; i++)
    Console.WriteLine($""строка {i} из 5"");
"
            },

            new Sample
            {
                Title = "3. Обход элементов и таблица",
                Code =
@"// All<type>() — возвращает все элементы указанного класса, например All<FamilyInstance>().
// Instances(category) — возвращает все экземпляры указанной категории,
// например Instances(BuiltInCategory.OST_Walls).
// Types(category) — возвращает все типоразмеры указанной категории, например Types(BuiltInCategory.OST_Floors).
//
// Дополнительно можно указать ID вида, чтобы ограничить выборку элементами, видимыми на этом виде:
// All<type>(viewId), Instances(viewId, category), Types(viewId, category).
// Например, All<Dimension>(ActiveView.Id) возвращает все размеры, видимые на текущем виде.
//
// Для All<type>() также доступны перегрузки с фильтрацией по категории:
// All<type>(category) и All<type>(viewId, category).
// Например, All<FamilyInstance>(BuiltInCategory.OST_StructuralColumns)
// возвращает все экземпляры семейств категории «Несущие колонны».
var walls = All<Wall>();
Print($""Найдено стен: {walls.Count}"");

Table(walls,
    w => w.Id,
    w => w.Name,
    w => Math.Round(w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble(), 2));
"
            },

            new Sample
            {
                Title = "4. Изменение модели в транзакции",
                Code =
@"// Транзакцию открываем сами — галочку «Автотранзакция» держите выключенной.
// Каждый Tx — один шаг в списке отмены. Если код внутри упадёт, весь шаг откатится.
var walls = All<Wall>();
if (walls.Count == 0) { Warn(""В модели нет стен.""); return; }

// Несколько действий пишем в фигурных скобках. Если код вернёт значение, его вернёт и Tx.
var marked = Tx(""Пометить стены комментарием"", () =>
{
    var count = 0;
    foreach (var wall in walls)
        if (SetComment(wall, ""проверено скриптом"")) count++;
    return count;
});
Ok($""Обновлено стен: {marked}"");

// Одно действие можно писать без скобок.
Tx(""Комментарий первой стене"", () => SetComment(walls[0], ""первая стена""));

// Метод без параметров можно передать по имени.
Tx(""Очистить комментарий последней стены"", ClearLast);

// Другой документ передаём первым. Автотранзакция открывается только в активном документе,
// так что это нужно и с включённой галочкой. Связанные файлы изменить нельзя.
// var other = App.OpenDocumentFile(@""C:\Projects\Other.rvt"");
// Tx(other, ""Пометить стены"", () => SetComment(other.ToElements<Wall>().First(), ""проверено""));
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
                Title = "5. Параметры выбранных элементов",
                Code =
@"// S0 возвращает первый элемент, S1 — второй, S2 — третий.
// Для получения элемента по индексу используйте S(i), где i — индекс элемента, начиная с 0.
if (Selection.Count == 0) { Warn(""Ничего не выбрано в модели.""); return; }

// ""-"" — параметр без значения; ""-пусто-"" — пустая строка.
foreach (var element in Selection)
    Dump(element);

// Другие варианты:
// Dump(S0);         — выбран один элемент
// Dump(S0, true);   — с параметрами типов
"
            },

            new Sample
            {
                Title = "6. Сравнение параметров элементов",
                Code =
@"// Выберите в модели от двух до пяти элементов: выводятся только параметры с разными значениями.
// Сравниваются только параметры, видимые в палитре свойств.
// ""-нет-"" (оранжевым) — у элемента такого параметра нет; ""-"" — параметр без значения; ""-пусто-"" — пустая строка.
if (Selection.Count < 2) { Warn(""Выберите в модели минимум два элемента.""); return; }

Compare(Selection);

// Другие варианты:
// Compare(S0, S1);            — два конкретных элемента
// Compare(S0, S1, S2);        — до пяти элементов через запятую
// Compare(Selection, true);   — с параметрами типов
"
            },

            new Sample
            {
                Title = "7. Показать геометрию на видах",
                Code =
@"// Show(...) рисует поверх модели, ничего не создавая в ней. Выберите стену или любой элемент.
// Нарисованное держится до следующего запуска; убрать раньше — ClearShown() или кнопка на панели.
if (Selection.Count == 0) { Warn(""Выберите элемент.""); return; }

var element = S0;
Show(element);                                    // вся геометрия: грани полупрозрачные, рёбра линиями
Show(element.get_BoundingBox(null), Orange);      // рамка
Show(element.Location, Red, ""расположение"");      // точка или линия расположения

if (element is Wall wall)
{
    var side = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).FirstOrDefault();
    if (side != null) Show(wall.GetGeometryObjectFromReference(side), Green, ""наружная грань"");
}

Show(XYZ.Zero, Purple, ""начало координат"");
"
            },

            new Sample
            {
                Title = "8. Полноценный IExternalCommand",
                Code =
@"// Если в коде есть объявление класса, плагин компилирует файл как есть.
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

        Console.WriteLine(""Команда запущена так же, как это делает Revit."");
        Console.WriteLine(""Документ: "" + doc.Title);

        using (var t = new Transaction(doc, ""Демо""))
        {
            t.Start();
            // ... изменения модели ...
            t.Commit();
        }

        return Result.Succeeded;
    }
}
"
            },

            new Sample
            {
                Title = "9. Свой класс с методом Main",
                Code =
@"using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// Параметры метода подставляются по типу: UIApplication, UIDocument,
// Document, Application, ExternalCommandData.
public class Report
{
    public void Main(Document doc, UIApplication uiapp)
    {
        Console.WriteLine(""Документ: "" + doc.Title);
        Console.WriteLine(""Версия: "" + uiapp.Application.VersionNumber);

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
                Title = "10. Подключение внешней библиотеки через #r",
                Code =
@"// Директива #r добавляет ссылку на сборку: по имени или по полному пути.
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
