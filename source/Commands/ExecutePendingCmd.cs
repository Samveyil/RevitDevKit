using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core.Running;
using RevitDevKit.Localization;

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

        // The last run, for Repeat Last: Debug.
        internal static RunRequest Last
        {
            get { lock (Sync) return _last; }
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
                TaskDialog.Show("DevKit", RibbonStrings.NothingToRepeat);
                return Result.Cancelled;
            }

            RevitTaskRunner.RunOne(request, commandData);
            return Result.Succeeded;
        }
    }
}
