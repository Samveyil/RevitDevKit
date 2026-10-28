using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RevitScriptRunner.Core
{
    /// <summary>
    /// Задание на выполнение в контексте Revit API.
    /// Контекст заданию не передаётся — код берёт его из RevitAPI.
    /// </summary>
    public sealed class RunRequest
    {
        public string Title { get; set; }
        public Func<object> Body { get; set; }

        /// <summary>Открыть транзакцию вокруг выполнения (для IExternalCommand — не нужно).</summary>
        public bool AutoTransaction { get; set; }

        public string TransactionName { get; set; }

        /// <summary>Вызывается в UI-потоке после завершения (успех / исключение).</summary>
        public Action<Exception> Completed { get; set; }
    }

    /// <summary>
    /// Мост между модальным-без-модальности окном плагина и главным потоком Revit.
    ///
    /// Revit API можно трогать только из его собственного потока и только в валидном
    /// контексте. Окно живёт отдельно, поэтому любой запуск проходит через
    /// ExternalEvent: кладём задание в очередь, дёргаем событие, Revit вызывает нас
    /// сам, когда ему удобно.
    /// </summary>
    public sealed class RevitTaskRunner : IExternalEventHandler
    {
        private static RevitTaskRunner _instance;
        private static ExternalEvent _event;

        private readonly ConcurrentQueue<RunRequest> _queue = new ConcurrentQueue<RunRequest>();

        /// <summary>
        /// ExternalCommandData текущего запуска — единственная часть контекста, которую
        /// нельзя получить из RevitAPI: её выдаёт сам Revit при вызове команды.
        ///
        /// Заполняется только на время одного запуска и сразу же обнуляется. Хранить
        /// её дольше нельзя: объект оборачивает нативный указатель, действительный
        /// лишь внутри IExternalCommand.Execute.
        /// </summary>
        internal static ExternalCommandData CurrentCommandData { get; private set; }

        /// <summary>Создаётся один раз при старте Revit — ExternalEvent требует контекста надстройки.</summary>
        public static void Initialize()
        {
            if (_instance != null) return;
            _instance = new RevitTaskRunner();
            _event = ExternalEvent.Create(_instance);
        }

        /// <summary>Ставит задание в очередь и просит Revit выполнить его.</summary>
        public static void Enqueue(RunRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");

            if (_event == null)
            {
                ConsoleOutput.Error("Script Runner не инициализирован — перезапустите Revit.");
                return;
            }

            _instance._queue.Enqueue(request);

            var state = _event.Raise();
            if (state != ExternalEventRequest.Accepted)
                ConsoleOutput.Warning("Revit отложил запуск (" + state + "). Закройте активные диалоги и попробуйте ещё раз.");
        }

        /// <summary>
        /// Единая точка запуска из окна плагина.
        ///
        /// Обычный скрипт идёт через ExternalEvent. Чужой IExternalCommand требует
        /// живой ExternalCommandData — его может дать только сам Revit, поэтому
        /// просим его выполнить нашу команду; если не вышло, честно предупреждаем
        /// и всё равно пробуем через событие.
        /// </summary>
        public static void Run(RunRequest request, bool needsCommandContext)
        {
            // Кнопка «Ещё раз» повторяет последний запуск независимо от того,
            // каким путём он пошёл.
            Commands.ExecutePendingCommand.Remember(request);

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
            return "Revit Script Runner";
        }

        public void Execute(UIApplication app)
        {
            RunRequest request;
            while (_queue.TryDequeue(out request))
                RunOne(request, null);
        }

        /// <summary>
        /// Выполняет задание прямо здесь и сейчас — контекст Revit API уже валиден.
        /// Вызывается из обработчика ExternalEvent (commandData = null) и из команды
        /// ленты, которая приносит свежий ExternalCommandData.
        /// </summary>
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

                    transaction = new Transaction(doc, request.TransactionName ?? "Script Runner");
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

                // Reflection заворачивает исключение скрипта — показываем настоящее.
                var real = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;

                ConsoleOutput.Exception(real, "Ошибка выполнения (" + sw.ElapsedMilliseconds + " мс):");
            }
            finally
            {
                // Держать ExternalCommandData дольше запуска нельзя — он протухает
                // вместе с вызовом команды, которая его принесла.
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