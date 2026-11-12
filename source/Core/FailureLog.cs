using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitDevKit.Core
{
    // While a script or command runs from DevKit, the warnings and errors Revit raises on committing a transaction
    // go to the console, with the elements they name. Revit still shows them as usual, unless "hide warnings" is
    // on: warnings are then deleted once written, and no warning dialog appears. Errors are left to Revit.
    public static class FailureLog
    {
        public static IDisposable Watch(bool hideWarnings)
        {
            Autodesk.Revit.ApplicationServices.Application application;
            try { application = RevitAPI.Application; }
            catch { application = null; }

            return application == null ? new Subscription(null, null) : Watch(application, hideWarnings);
        }

        public static IDisposable Watch(Autodesk.Revit.ApplicationServices.Application application, bool hideWarnings)
        {
            EventHandler<FailuresProcessingEventArgs> handler = (s, e) => OnFailures(e.GetFailuresAccessor(), hideWarnings);
            application.FailuresProcessing += handler;
            return new Subscription(application, handler);
        }

        private static void OnFailures(FailuresAccessor accessor, bool hideWarnings)
        {
            try
            {
                var document = accessor.GetDocument();
                var transaction = accessor.GetTransactionName();

                // As Revit's warnings dialog: the same failure raised several times is one block, its elements by occurrence.
                var groups = accessor.GetFailureMessages()
                    .Where(m => m.GetSeverity() != FailureSeverity.None)
                    .GroupBy(m => new { Severity = m.GetSeverity(), Definition = m.GetFailureDefinitionId().Guid, Text = m.GetDescriptionText() })
                    .ToList();

                foreach (var group in groups)
                {
                    var warning = group.Key.Severity == FailureSeverity.Warning;
                    var hidden = hideWarnings && warning;
                    var occurrences = group
                        .Select(m => (IList<string>)m.GetFailingElementIds().Select(id => Describe(document, id)).ToList())
                        .ToList();

                    ConsoleOutput.WriteWrapped(Format(warning, transaction, group.Key.Text, occurrences, hidden),
                        warning ? LogLevel.Warning : LogLevel.Error);

                    if (hidden)
                    {
                        foreach (var message in group) accessor.DeleteWarning(message);
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Сбои Revit не прочитаны: " + ex.Message);
            }
        }

        // Предупреждение · Транзакция «Пометить стены»
        //   Выделенные стены перекрываются. …
        //   Элементы (2):
        //     Стены · Базовая стена · Наружная - 300 : Id 412873
        // The same failure raised several times: "· 3 раза" in the header, and the elements numbered by occurrence.
        //   1) Стены · … : Id 412873
        //      Стены · … : Id 412874
        internal static string Format(bool warning, string transaction, string text, IList<IList<string>> occurrences, bool hidden)
        {
            var count = occurrences?.Count ?? 0;
            var block = new StringBuilder();
            block.Append(warning ? "Предупреждение" : "Ошибка");
            if (!string.IsNullOrEmpty(transaction)) block.Append(" · Транзакция «").Append(transaction).Append('»');
            if (count > 1) block.Append(" · ").Append(Times(count));
            block.AppendLine();

            foreach (var line in (text ?? string.Empty).Trim().Replace("\r\n", "\n").Split('\n'))
                block.Append("  ").AppendLine(line.Trim());

            if (!warning) block.AppendLine("  Если ошибку не решить, Revit отменит транзакцию.");
            else if (hidden) block.AppendLine("  Скрыто: окно Revit не показано.");

            if (count == 1)
            {
                var elements = occurrences[0];
                if (elements.Count > 0)
                {
                    block.Append("  Элементы (").Append(elements.Count).AppendLine("):");
                    foreach (var element in elements)
                        block.Append("    ").AppendLine(element);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var number = (i + 1) + ") ";
                    var elements = occurrences[i].Count > 0 ? occurrences[i] : new[] { "без элементов" };
                    for (var j = 0; j < elements.Count; j++)
                        block.Append("  ").Append(j == 0 ? number : new string(' ', number.Length)).AppendLine(elements[j]);
                }
            }

            return block.ToString();
        }

        // 2 раза, 5 раз, 21 раз, 22 раза.
        private static string Times(int n)
        {
            var tens = n % 100;
            var ones = n % 10;
            var word = tens >= 11 && tens <= 14 ? "раз" : ones >= 2 && ones <= 4 ? "раза" : "раз";
            return n + " " + word;
        }

        // "Стены · Базовая стена · Наружная - 300 : Id 412873": category, family and type, or the element's name
        // when it has no type. An element already gone (the transaction rolled back) is just its Id.
        private static string Describe(Document document, ElementId id)
        {
            var parts = new List<string>();
            try
            {
                var element = document?.GetElement(id);
                if (element != null)
                {
                    parts.Add(element.Category?.Name);
                    if (document.GetElement(element.GetTypeId()) is ElementType type)
                    {
                        parts.Add(type.FamilyName);
                        parts.Add(type.Name);
                    }
                    else
                    {
                        parts.Add(element.Name);
                    }
                }
            }
            catch
            {
                // Read what can be read; the Id is always there.
            }

            var name = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
            return (name.Length > 0 ? name + " : " : string.Empty) + "Id " + Id(id);
        }

#if R2024_OR_GREATER
        private static string Id(ElementId id) => id.Value.ToString();
#else
        private static string Id(ElementId id) => id.IntegerValue.ToString();
#endif

        private sealed class Subscription : IDisposable
        {
            private Autodesk.Revit.ApplicationServices.Application _application;
            private readonly EventHandler<FailuresProcessingEventArgs> _handler;

            public Subscription(Autodesk.Revit.ApplicationServices.Application application, EventHandler<FailuresProcessingEventArgs> handler)
            {
                _application = application;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_application == null) return;
                try { _application.FailuresProcessing -= _handler; }
                catch (Exception ex) { ConsoleOutput.Debug("Отписка от сбоев Revit: " + ex.Message); }
                _application = null;
            }
        }
    }
}
