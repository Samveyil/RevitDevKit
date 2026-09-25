using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Generic;

namespace RevitScriptRunner.Core
{
    /// <summary>
    /// Запуск задания через настоящую команду Revit.
    ///
    /// Зачем это нужно. Окно плагина немодальное, а ExternalCommandData живёт только
    /// внутри вызова IExternalCommand.Execute — держать его между запусками нельзя.
    /// Поэтому для чужих IExternalCommand мы просим Revit выполнить нашу собственную
    /// команду (PostCommand) и получаем свежий, честный ExternalCommandData.
    ///
    /// Идентификатор кнопки на ленте Revit формирует сам, по схеме
    /// CustomCtrl_%CustomCtrl_%{вкладка}%{панель}%{имя кнопки}. Схема недокументирована,
    /// поэтому пробуем несколько вариантов и молча откатываемся на ExternalEvent.
    /// </summary>
    internal static class RevitCommandBridge
    {
        internal const string RunButtonName = "RsrRunPending";

        private static RevitCommandId _commandId;
        private static bool _resolved;

        /// <summary>Доступен ли запуск в контексте настоящей команды Revit.</summary>
        public static bool IsAvailable
        {
            get { return Resolve() != null; }
        }

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
                    // неверный формат идентификатора — пробуем следующий
                }
            }

            ConsoleOutput.Debug("Команда ленты не найдена — IExternalCommand будет запускаться через ExternalEvent.");
            return null;
        }

        /// <summary>Имя команды в манифесте — оно же пункт «Внешние инструменты».</summary>
        private const string ExternalToolText = "Script Runner Run Again";

        private static IEnumerable<string> Candidates()
        {
            // Кнопка на собственной вкладке — основной вариант.
            yield return "CustomCtrl_%CustomCtrl_%" + App.TabName + "%" + App.PanelName + "%" + RunButtonName;
            yield return "CustomCtrl_%" + App.TabName + "%" + App.PanelName + "%" + RunButtonName;

            // Запасной: пункт в «Надстройки → Внешние инструменты» из манифеста.
            yield return "CustomCtrl_%CustomCtrl_%Add-Ins%External Tools%" + ExternalToolText;
            yield return "CustomCtrl_%CustomCtrl_%Add-Ins%" + ExternalToolText;
        }

        /// <summary>
        /// Пытается выполнить задание внутри настоящей команды Revit.
        /// Возвращает false, если это невозможно — тогда зовите RevitTaskRunner.Enqueue.
        /// </summary>
        public static bool TryPost(RunRequest request)
        {
            if (request == null) return false;

            var id = Resolve();
            if (id == null) return false;

            try
            {
                var uiApp = RevitAPI.UIApplication;

                if (uiApp == null || !uiApp.CanPostCommand(id)) return false;

                Commands.ExecutePendingCommand.SetPending(request);
                uiApp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                Commands.ExecutePendingCommand.SetPending(null);
                ConsoleOutput.Debug("PostCommand не сработал (" + ex.Message + "), используем ExternalEvent.");
                return false;
            }
        }
    }
}