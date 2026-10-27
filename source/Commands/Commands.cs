using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitScriptRunner.Core;

namespace RevitScriptRunner.Commands
{
    /// <summary>
    /// Открывает окно Script Runner.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenRunnerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ConsoleOutput.Install();
            RevitTaskRunner.Initialize();

            try
            {
                UI.RunnerWindow.ShowSingleton();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ConsoleOutput.Exception(ex, "Не удалось открыть окно Script Runner:");
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Открывает окно загрузки внешних DLL.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenAssemblyRunnerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ConsoleOutput.Install();
            RevitTaskRunner.Initialize();

            try
            {
                UI.AssemblyRunnerWindow.ShowSingleton();
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

    /// <summary>
    /// Показывает прикрепляемую панель с консолью плагина.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ShowConsolePaneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UI.ConsolePane.Show(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Панель консоли недоступна: " + ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Выполняет отложенное задание внутри настоящей команды Revit.
    ///
    /// Окно плагина немодальное, а ExternalCommandData действителен только на время
    /// вызова Execute. Поэтому задание кладётся в «карман», а Revit сам вызывает эту
    /// команду через PostCommand — и отдаёт свежий контекст.
    ///
    /// Кнопка на ленте («Ещё раз») нужна ещё и технически: без неё у команды нет
    /// идентификатора, который принимает PostCommand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExecutePendingCommand : IExternalCommand
    {
        private static readonly object Sync = new object();
        private static RunRequest _pending;
        private static RunRequest _last;

        /// <summary>
        /// Кладёт задание в «карман» — его заберёт ближайший вызов от Revit по PostCommand.
        /// </summary>
        internal static void SetPending(RunRequest request)
        {
            lock (Sync) _pending = request;
        }

        /// <summary>
        /// Запоминает запуск для кнопки «Ещё раз». Вызывается для любого запуска —
        /// и для скрипта через ExternalEvent, и для команды через PostCommand.
        /// </summary>
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
                TaskDialog.Show("Script Runner",
                    "Нечего повторять. Откройте Script Runner и запустите скрипт хотя бы один раз.");
                return Result.Cancelled;
            }

            RevitTaskRunner.RunOne(request, commandData);
            return Result.Succeeded;
        }
    }
}
