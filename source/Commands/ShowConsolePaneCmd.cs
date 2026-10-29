using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace RevitDevKit.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ShowConsolePaneCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UI.Console.ConsolePane.Show(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Панель консоли недоступна: " + ex.Message;
                return Result.Failed;
            }
        }
    }
}
