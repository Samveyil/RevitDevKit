using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core;

namespace RevitDevKit.Commands
{
    // Repeat Last: Debug. The last DLL command again, under the open Visual Studio, so breakpoints set in the
    // project are hit. A script is debugged in Script Editor instead: Repeat Last does not compile it again, and
    // only a script compiled under the debugger maps its lines to its file.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugLastCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var request = ExecutePendingCmd.Last;
            if (request == null)
            {
                TaskDialog.Show("DevKit", "Нечего повторять. Запустите команду в Add-in Explorer хотя бы один раз.");
                return Result.Cancelled;
            }

            if (request.SourceDll == null)
            {
                TaskDialog.Show("DevKit", "Последним запускался скрипт. Скрипт отлаживается в Script Editor, кнопкой с жуком.");
                return Result.Cancelled;
            }

            // Without a debugger nothing runs: the user has been told to open Visual Studio.
            if (!DebugLaunch.TryAttach(request.SourceDll))
                return Result.Cancelled;

            RevitTaskRunner.RunOne(request, commandData);
            return Result.Succeeded;
        }
    }
}
