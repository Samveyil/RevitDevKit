using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDevKit.Core
{
    // While a script or command runs from DevKit, the warnings and errors Revit raises on committing a transaction
    // go to the console, with the elements they name. Revit still shows them as usual, unless "hide warnings" is
    // on: warnings are then deleted once written, and no warning dialog appears. Errors are left to Revit.
    public static class FailureLog
    {
        private const int ShownIds = 10;

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
                var transaction = accessor.GetTransactionName();
                foreach (var message in accessor.GetFailureMessages())
                {
                    var severity = message.GetSeverity();
                    if (severity == FailureSeverity.None) continue;

                    var hidden = hideWarnings && severity == FailureSeverity.Warning;
                    Write(severity, transaction, message.GetDescriptionText(), message.GetFailingElementIds(), hidden);

                    if (hidden) accessor.DeleteWarning(message);
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Сбои Revit не прочитаны: " + ex.Message);
            }
        }

        // Предупреждение · «Пометить стены»: Выделенные стены перекрываются. (2): 412873, 412990
        internal static string Line(FailureSeverity severity, string transaction, string text, ICollection<ElementId> ids, bool hidden)
        {
            var kind = severity == FailureSeverity.Warning ? "Предупреждение" : "Ошибка";
            var line = kind + (string.IsNullOrEmpty(transaction) ? "" : " · «" + transaction + "»") + ": " + (text ?? "").Trim();

            if (ids != null && ids.Count > 0)
            {
                line += " (" + ids.Count + "): " + string.Join(", ", ids.Take(ShownIds).Select(Id));
                if (ids.Count > ShownIds) line += " и ещё " + (ids.Count - ShownIds);
            }

            if (severity != FailureSeverity.Warning)
                line += " — если её не решить, Revit отменит транзакцию";
            else if (hidden)
                line += " — скрыто";

            return line;
        }

        private static void Write(FailureSeverity severity, string transaction, string text, ICollection<ElementId> ids, bool hidden) =>
            ConsoleOutput.WriteLine(Line(severity, transaction, text, ids, hidden), severity == FailureSeverity.Warning ? LogLevel.Warning : LogLevel.Error);

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
