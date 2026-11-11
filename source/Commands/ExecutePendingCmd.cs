using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core;

namespace RevitDevKit.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExecutePendingCmd : IExternalCommand
    {
        private static readonly object Sync = new object();
        private static RunRequest _pending;
        private static RunRequest _last;

        internal static void SetPending(RunRequest request)
        {
            lock (Sync) _pending = request;
        }

        internal static void Remember(RunRequest request)
        {
            if (request == null) return;
            lock (Sync) _last = request;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RunRequest request;
            lock (Sync)
            {
                request = _pending ?? _last;
                _pending = null;
            }

            if (request == null)
            {
                TaskDialog.Show("DevKit",
                    "Нечего повторять. Запустите скрипт в Script Editor или команду в Add-in Explorer хотя бы один раз.");
                return Result.Cancelled;
            }

            RevitTaskRunner.RunOne(request, commandData);
            return Result.Succeeded;
        }
    }
}
