using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RevitDevKit.Localization;

namespace RevitDevKit.Core.Running
{
    // While a script or command runs from DevKit, the warnings and errors Revit raises on committing a transaction
    // go to the console, with the elements they name. Revit still shows them as usual, unless quiet mode is
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
                ConsoleOutput.Debug(string.Format(ConsoleStrings.FailuresNotRead, ex.Message));
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
            block.Append(warning ? ConsoleStrings.FailureWarning : ConsoleStrings.FailureError);
            if (!transaction.IsNullOrEmpty()) block.Append(string.Format(ConsoleStrings.FailureTransaction, transaction));
            if (count > 1) block.Append(" · ").Append(Times(count));
            block.AppendLine();

            foreach (var line in (text ?? string.Empty).Trim().Replace("\r\n", "\n").Split('\n'))
                block.Append("  ").AppendLine(line.Trim());

            if (!warning) block.AppendLine(ConsoleStrings.FailureRollsBack);
            else if (hidden) block.AppendLine(ConsoleStrings.FailureQuietMode);

            if (count == 1)
            {
                var elements = occurrences[0];
                if (elements.Count > 0)
                {
                    block.AppendLine(string.Format(ConsoleStrings.FailureElements, elements.Count));
                    foreach (var element in elements)
                        block.Append("    ").AppendLine(element);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var number = (i + 1) + ") ";
                    var elements = occurrences[i].Count > 0 ? occurrences[i] : new[] { ConsoleStrings.FailureNoElements };
                    for (var j = 0; j < elements.Count; j++)
                        block.Append("  ").Append(j == 0 ? number : new string(' ', number.Length)).AppendLine(elements[j]);
                }
            }

            return block.ToString();
        }

        // "3 times"; in Russian 2 раза, 5 раз, 21 раз, 22 раза: the Russian text has a form for 2, 3 and 4 and one for the rest.
        private static string Times(int n)
        {
            var tens = n % 100;
            var ones = n % 10;
            var few = !(tens >= 11 && tens <= 14) && ones >= 2 && ones <= 4;
            return string.Format(few ? ConsoleStrings.TimesFew : ConsoleStrings.TimesMany, n);
        }

        // "Стены · Базовая стена · Наружная - 300 : Id 412873": category, family and type, or the element's name
        // when it has no type. An element already gone (the transaction rolled back) is just its Id.
        private static string Describe(Document document, ElementId id)
        {
            var parts = new List<string>();
            try
            {
                var element = id.ToElement(document);
                if (element != null)
                {
                    parts.Add(element.Category?.Name);
                    if (element.ToElementType() is ElementType type)
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

            var name = string.Join(" · ", parts.Where(p => !p.IsNullOrWhiteSpace()).Distinct());
            return (name.Length > 0 ? name + " : " : string.Empty) + "Id " + Id(id);
        }

        private static string Id(ElementId id) => id.GetValue().ToString();

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
                catch (Exception ex) { ConsoleOutput.Debug(string.Format(ConsoleStrings.FailuresUnsubscribe, ex.Message)); }
                _application = null;
            }
        }
    }
}
