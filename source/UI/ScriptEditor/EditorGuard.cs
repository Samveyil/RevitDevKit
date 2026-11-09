using RevitDevKit.Core;
using System;

namespace RevitDevKit.UI.ScriptEditor
{
    // The editor's key and text handlers run on Revit's UI thread, where an exception escaping a handler takes
    // Revit down. They run through here: an error is written to the console, and the editor keeps working.
    internal static class EditorGuard
    {
        public static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning("Ошибка в редакторе скриптов (скобки или подсказки): " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
