using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using RevitDevKit.Commands;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace RevitDevKit.Core
{
    public sealed class RunRequest
    {
        public string Title { get; set; }
        public Func<object> Body { get; set; }

        public bool AutoTransaction { get; set; }

        public string TransactionName { get; set; }

        public Action<Exception> Completed { get; set; }

        // The DLL a command of Add-in Explorer comes from; null for a script. Repeat Last: Debug repeats only
        // commands, and attaches the Visual Studio whose solution holds this DLL.
        public string SourceDll { get; set; }
    }

    public sealed class RevitTaskRunner : IExternalEventHandler
    {
        private static RevitTaskRunner _instance;
        private static ExternalEvent _event;

        private readonly ConcurrentQueue<RunRequest> _queue = new ConcurrentQueue<RunRequest>();

        internal static ExternalCommandData CurrentCommandData { get; private set; }

        public static void Initialize()
        {
            if (_instance != null) return;
            _instance = new RevitTaskRunner();
            _event = ExternalEvent.Create(_instance);
        }

        public static void Enqueue(RunRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");

            if (_event == null)
            {
                ConsoleOutput.Error("DevKit не инициализирован — перезапустите Revit.");
                return;
            }

            _instance._queue.Enqueue(request);

            var state = _event.Raise();
            if (state != ExternalEventRequest.Accepted)
                ConsoleOutput.Warning("Revit отложил запуск (" + state + "). Закройте активные диалоги и попробуйте ещё раз.");
        }

        public static void Run(RunRequest request, bool needsCommandContext)
        {
            ExecutePendingCmd.Remember(request);

            if (needsCommandContext)
            {
                if (RevitCommandBridge.TryPost(request))
                    return;

                ConsoleOutput.Warning(
                    "Не удалось получить контекст команды Revit. IExternalCommand будет запущен " +
                    "через ExternalEvent — commandData окажется недоступен.");
            }

            Enqueue(request);
        }

        public string GetName() => "Revit DevKit";

        public void Execute(UIApplication app)
        {
            RunRequest request;
            while (_queue.TryDequeue(out request))
                RunOne(request, null);
        }

        internal static void RunOne(RunRequest request, ExternalCommandData commandData)
        {
            CurrentCommandData = commandData;

            var sw = Stopwatch.StartNew();
            Exception failure = null;

            ConsoleOutput.EnsureTraceListener();
            ConsoleOutput.Banner("Запуск: " + (request.Title ?? "скрипт"));

            Transaction transaction = null;
            try
            {
                if (request.AutoTransaction)
                {
                    var doc = RevitAPI.Document;
                    if (doc == null)
                        throw new InvalidOperationException("Автотранзакция включена, но активного документа нет.");

                    transaction = new Transaction(doc, request.TransactionName ?? "DevKit");
                    transaction.Start();
                }

                var result = request.Body();

                if (transaction != null)
                {
                    transaction.Commit();
                    ConsoleOutput.Debug("Транзакция закоммичена.");
                }

                sw.Stop();
                ReportResult(result, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                failure = ex;

                if (transaction != null && transaction.GetStatus() == TransactionStatus.Started)
                {
                    transaction.RollBack();
                    ConsoleOutput.Warning("Транзакция откачена.");
                }

                var real = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;

                ConsoleOutput.Exception(real, "Ошибка выполнения (" + sw.ElapsedMilliseconds + " мс):");
            }
            finally
            {
                CurrentCommandData = null;

                if (transaction != null) transaction.Dispose();

                if (request.Completed != null)
                {
                    try { request.Completed(failure); }
                    catch (Exception ex) { ConsoleOutput.Exception(ex, "Ошибка в обработчике завершения:"); }
                }
            }
        }

        private static void ReportResult(object result, long ms)
        {
            if (result == null)
            {
                ConsoleOutput.Success("Готово за " + ms + " мс.");
                return;
            }

            if (result is Result revitResult)
            {
                var level = revitResult == Result.Succeeded ? LogLevel.Success : LogLevel.Warning;
                ConsoleOutput.WriteLine("Команда вернула " + revitResult + " (" + ms + " мс).", level);
                return;
            }

            ConsoleOutput.WriteLine("Результат: " + ConsoleOutput.Format(result), LogLevel.Success);
            ConsoleOutput.Success("Готово за " + ms + " мс.");
        }
    }

    // ExternalCommandData exists only inside IExternalCommand.Execute, so a modeless window
    // cannot keep it. To run a foreign IExternalCommand we PostCommand our own ribbon
    // button (Repeat Last) and execute the pending request inside it.
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

                ExecutePendingCmd.SetPending(request);
                uiApp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                ExecutePendingCmd.SetPending(null);
                ConsoleOutput.Debug("PostCommand не сработал (" + ex.Message + "), используем ExternalEvent.");
                return false;
            }
        }
    }
}
