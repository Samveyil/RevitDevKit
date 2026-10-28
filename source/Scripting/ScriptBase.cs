using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core;

namespace RevitDevKit.Scripting
{
    /// <summary>
    /// Базовый класс, в который «заворачивается» короткий скрипт.
    /// Даёт доступ к контексту Revit и к консоли без лишних церемоний.
    /// Полноценные классы пользователя могут наследоваться от него явно.
    ///
    /// Контекст здесь не хранится и никем не заполняется — он берётся из
    /// <see cref="RevitAPI"/> в момент обращения. Поэтому свойства всегда отражают
    /// текущее состояние Revit, даже если пользователь переключил документ, пока
    /// скрипт выполнялся.
    /// </summary>
    public abstract class ScriptBase
    {
        public UIApplication UIApp { get => RevitAPI.UIApplication; }
        public Autodesk.Revit.ApplicationServices.Application App { get => RevitAPI.Application; }
        public UIDocument UIDoc { get => RevitAPI.UIDocument; }
        public Document Doc { get => RevitAPI.Document; }
        public View ActiveView { get => RevitAPI.ActiveView; }
        public ExternalCommandData CommandData { get => RevitTaskRunner.CurrentCommandData; }


        // ---------- консоль ----------
        public void Print(object value) => ConsoleOutput.WriteObject(value);
        public void Print(string format, params object[] args)
        {
            ConsoleOutput.WriteLine(args == null || args.Length == 0 ? format : string.Format(format, args));
        }

        public void Ok(string text) => ConsoleOutput.Success(text);
        public void Warn(string text) => ConsoleOutput.Warning(text);
        public void Fail(string text) => ConsoleOutput.Error(text);
        public void Clear() => ConsoleOutput.Clear();

        /// <summary>Печатает таблицу по коллекции: заголовок + строки.</summary>
        public void Table<T>(IEnumerable<T> items, params Func<T, object>[] columns)
        {
            if (items == null)
            {
                Print("null");
                return;
            }
            
            foreach (var item in items)
            {
                if (columns == null || columns.Length == 0)
                {
                    Print(item);
                    continue;
                }
                var cells = columns.Select(c =>
                {
                    try { return ConsoleOutput.Format(c(item)); }
                    catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
                });
                ConsoleOutput.WriteLine(string.Join("  |  ", cells));
            }
        }

        // ---------- транзакции ----------

        /// <summary>Выполняет действие внутри транзакции и коммитит её.</summary>
        public void Tx(string name, Action action)
        {
            if (action == null)
                throw new ArgumentNullException("action");

            // Берём документ один раз: свойство каждый раз спрашивает Revit заново.
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

        // ---------- частые выборки ----------


        /// <summary>Все экземпляры указанной категории в текущем документе.</summary>
        public IList<Element> Instances(BuiltInCategory category) => Doc.ToElements(category);

        /// <summary>Все экземпляры указанной категории в текущем документе.</summary>
        public IList<Element> Instances(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category);

        /// <summary>Все экземпляры указанной категории в текущем документе.</summary>
        public IList<Element> Types(BuiltInCategory category) => Doc.ToElements(category, true);

        /// <summary>Все экземпляры указанной категории в текущем документе.</summary>
        public IList<Element> Types(ElementId viewId, BuiltInCategory category) => Doc.ToElements(viewId, category, true);

        /// <summary>Все элементы указанного типа Revit API в текущем документе.</summary>
        public IList<T> All<T>() where T : Element => Doc.ToElements<T>();

        /// <summary>Все элементы указанного типа Revit API в текущем документе.</summary>
        public IList<T> All<T>(ElementId viewId) where T : Element => Doc.ToElements<T>(viewId);

        /// <summary>Все элементы указанного типа Revit API и категории в текущем документе.</summary>
        public IList<T> All<T>(BuiltInCategory category) where T : Element => Doc.ToElements<T>(category);

        /// <summary>Все элементы указанного типа Revit API и категории в текущем документе.</summary>
        public IList<T> All<T>(ElementId viewId, BuiltInCategory category) where T : Element => Doc.ToElements<T>(viewId, category);


        /// <summary>Выбранные в модели элементы.</summary>
        public ICollection<Element> Selection { get => UIDoc.ToSelectedElements(); }

        /// <summary>Первый выбранный элемент или null, если ничего не выбрано.</summary>
        public Element S0 { get => S(0); }

        /// <summary>Первый выбранный элемент или null, если ничего не выбрано.</summary>
        public Element S1 { get => S(1); }

        /// <summary>Первый выбранный элемент или null, если ничего не выбрано.</summary>
        public Element S2 { get => S(2); }


        /// <summary>
        /// Выбранный элемент по номеру или null, если столько не выбрано.
        /// Создаёт только запрошенный элемент, а не весь список.
        /// </summary>
        public Element S(int index)
        {
            if (index < 0) return null;

            return Selection.Skip(index).FirstOrDefault();
        }

        /// <summary>
        /// То же, но сразу приведённый к типу. Возвращает null, если столько не
        /// выбрано или тип не совпал, — избавляет от ручного приведения.
        /// </summary>
        public T S<T>(int index) where T : Element
        {
            return S(index) as T;
        }


        /// <summary>Печатает все параметры элемента — быстрый способ «заглянуть внутрь».</summary>
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

            var parameters = element.Parameters.Cast<Parameter>().ToList();
            if (useType)
            {
                parameters.AddRange(element.ToElementType().Parameters.Cast<Parameter>());
            }

            foreach (Parameter p in parameters.OrderBy(p => p.Definition == null ? "" : p.Definition.Name))
            {
                if (p.Definition == null) continue;
                string value;
                try
                {
                    if (p.HasValue)
                    {
                        value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                    }
                    else
                        value = "-";
                }
                catch { value = "<ошибка чтения>"; }
                ConsoleOutput.WriteLine("  " + p.Definition.Name.PadRight(38) + " = " + value);
            }
        }
    }
}