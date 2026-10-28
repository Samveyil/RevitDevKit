using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using RevitDevKit.Commands;
using System;
using System.Collections.Generic;

namespace RevitDevKit.Core
{
    // ExternalCommandData exists only inside IExternalCommand.Execute, so a modeless window
    // cannot keep it. To run a foreign IExternalCommand we PostCommand our own ribbon
    // button (Run Again) and execute the pending request inside it.
    internal static class RevitCommandBridge
    {
        private static RevitCommandId _commandId;
        private static bool _resolved;

        private static RevitCommandId Resolve()
        {
            if (_resolved) return _commandId;
            _resolved = true;

            foreach (var candidate in Candidates())
            {
                try
                {
                    var id = RevitCommandId.LookupCommandId(candidate);
                    if (id == null) continue;

                    _commandId = id;
                    ConsoleOutput.Debug("Контекст команды Revit доступен: " + candidate);
                    return _commandId;
                }
                catch
                {
                    // invalid id format - try the next candidate
                }
            }

            ConsoleOutput.Debug("Команда ленты не найдена — IExternalCommand будет запускаться через ExternalEvent.");
            return null;
        }

        // Ribbon command ids are undocumented: CustomCtrl_%...%{tab}%{panel}%{pulldown}%{button},
        // where every segment is the internal Name, not the caption.
        private static IEnumerable<string> Candidates()
        {
            var btnName = typeof(ExecutePendingCmd).Name;

            yield return $"CustomCtrl_%CustomCtrl_%CustomCtrl_%Add-Ins%{App.PanelName}%{App.PanelName}%{btnName}";
            yield return $"CustomCtrl_%CustomCtrl_%Add-Ins%{App.PanelName}%{App.PanelName}%{btnName}";
        }

        public static bool TryPost(RunRequest request)
        {
            if (request == null) return false;

            var id = Resolve();
            if (id == null) return false;

            try
            {
                var uiApp = RevitAPI.UIApplication;

                if (uiApp == null || !uiApp.CanPostCommand(id)) return false;

                Commands.ExecutePendingCmd.SetPending(request);
                uiApp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                Commands.ExecutePendingCmd.SetPending(null);
                ConsoleOutput.Debug("PostCommand не сработал (" + ex.Message + "), используем ExternalEvent.");
                return false;
            }
        }
    }
}
