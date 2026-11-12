using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Running;
using System;

namespace RevitDevKit.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenAssemblyRunnerCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ConsoleOutput.Install();
            RevitTaskRunner.Initialize();

            try
            {
                UI.DllRunner.AssemblyRunnerWindow.ShowSingleton();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ConsoleOutput.Exception(ex, "Не удалось открыть окно загрузки DLL:");
                return Result.Failed;
            }
        }
    }
}
