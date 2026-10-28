using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RevitDevKit.Core
{
    // The Revit API may only be called on Revit's thread in a valid context, so every run
    // from the modeless windows goes through ExternalEvent (or PostCommand, see RevitCommandBridge).
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
            if (request == null) throw new ArgumentNullException("request");

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
            Commands.ExecutePendingCmd.Remember(request);

            if (needsCommandContext)
            {
                if (RevitCommandBridge.TryPost(request)) return;

                ConsoleOutput.Warning(
                    "Не удалось получить контекст команды Revit. IExternalCommand будет запущен " +
                    "через ExternalEvent — commandData окажется недоступен.");
            }

            Enqueue(request);
        }

        public string GetName()
        {
            return "Revit DevKit";
        }

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
}
