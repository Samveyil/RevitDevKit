using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Running;
using System;
using RevitDevKit.Localization;

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
                UI.ScriptEditor.ScriptEditorWindow.ShowSingleton();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ConsoleOutput.Exception(ex, RibbonStrings.ScriptEditorOpenFailed);
                return Result.Failed;
            }
        }
    }
}
