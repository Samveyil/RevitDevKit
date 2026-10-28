using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core;

namespace RevitDevKit.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenScriptEditorCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ConsoleOutput.Install();
            RevitTaskRunner.Initialize();

            try
            {
                UI.ScriptEditorWindow.ShowSingleton();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ConsoleOutput.Exception(ex, "Не удалось открыть окно Script Editor:");
                return Result.Failed;
            }
        }
    }
}
