using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using RevitDevKit.Commands;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Show;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using RevitDevKit.Localization;

namespace RevitDevKit.Core.Running
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

        // Warnings Revit raises while it runs are written to the console and deleted, so no dialog shows.
        public bool HideWarnings { get; set; }

        // Called in the API context once the run is over, with what it wrote: Add-in Explorer opens the console
        // pane from here when the run calls for it (ConsoleOptions.OpenPane).
        public Action<RunReport> Reported { get; set; }
    }

    // What a run left in the console.
    public sealed class RunReport
    {
        // It threw, or the command returned Failed.
        public bool Failed { get; set; }

        // Revit or the run wrote a warning or an error, failures of its transactions among them.
        public bool HasProblems { get; set; }

        // The run itself wrote something: Console, Debug, Trace, Show.
        public bool HasOutput { get; set; }
    }

    public sealed class RevitTaskRunner : IExternalEventHandler
    {
        private static RevitTaskRunner _instance;
        private static ExternalEvent _event;

        private readonly ConcurrentQueue<RunRequest> _queue = new ConcurrentQueue<RunRequest>();

        // Small things that need an API context and say nothing in the console, such as redrawing the views.
        private readonly ConcurrentQueue<Action<UIApplication>> _actions = new ConcurrentQueue<Action<UIApplication>>();

        public static void Post(Action<UIApplication> action)
        {
            if (action == null || _event == null) return;
            _instance._actions.Enqueue(action);
            _event.Raise();
        }

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
                ConsoleOutput.Error(ConsoleStrings.NotInitialized);
                return;
            }

            _instance._queue.Enqueue(request);

            var state = _event.Raise();
            if (state != ExternalEventRequest.Accepted)
                ConsoleOutput.Warning(string.Format(ConsoleStrings.RunDeferred, state));
        }

        public static void Run(RunRequest request, bool needsCommandContext)
        {
            ExecutePendingCmd.Remember(request);

            if (needsCommandContext)
            {
                if (RevitCommandBridge.TryPost(request))
                    return;

                ConsoleOutput.Warning(ConsoleStrings.NoCommandContext);
            }

            Enqueue(request);
        }

        public string GetName() => "Revit DevKit";

        public void Execute(UIApplication app)
        {
            while (_actions.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex) { ConsoleOutput.Debug("DevKit: " + ex.Message); }
            }

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
            ConsoleOutput.Banner(string.Format(ConsoleStrings.RunBanner, request.Title ?? ConsoleStrings.DefaultRunTitle));

            // The views show what this run shows, with Show(...) in a script or Debug.WriteLine(geometry) in a DLL;
            // with the option off, what earlier runs showed stays to compare with.
            if (ConsoleOptions.ClearShownOnRun)
                ShownGeometry.Clear();

            // What the run writes, for Reported: warnings and errors until it ends, other output only from the body.
            // DevKit's own lines (the banner, "Готово", "Команда вернула Cancelled") do not count.
            var report = new RunReport();
            var inBody = false;
            var counting = true;
            EventHandler<LogChunkEventArgs> watch = (s, e) =>
            {
                if (!counting) return;
                if (e.Level == LogLevel.Warning || e.Level == LogLevel.Error)
                    report.HasProblems = true;
                else if (inBody && e.Level != LogLevel.System)
                    report.HasOutput = true;
            };
            ConsoleOutput.ChunkWritten += watch;

            // Only transactions committed while this request runs: not the user's, nor other add-ins' between runs.
            var failures = FailureLog.Watch(request.HideWarnings);
            Transaction transaction = null;
            try
            {
                if (request.AutoTransaction)
                {
                    var doc = RevitAPI.Document;
                    if (doc == null)
                        throw new InvalidOperationException(ConsoleStrings.AutoTransactionNoDocument);

                    transaction = new Transaction(doc, request.TransactionName ?? "DevKit");
                    transaction.Start();
                }

                inBody = true;
                object result;
                try { result = request.Body(); }
                finally { inBody = false; }

                if (result is Result returned && returned == Result.Failed)
                    report.Failed = true;

                if (transaction != null)
                {
                    transaction.Commit();
                    ConsoleOutput.Debug(ConsoleStrings.TransactionCommitted);
                }

                sw.Stop();
                counting = false;
                ReportResult(result, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                failure = ex;
                report.Failed = true;

                if (transaction != null && transaction.GetStatus() == TransactionStatus.Started)
                {
                    transaction.RollBack();
                    ConsoleOutput.Warning(ConsoleStrings.TransactionRolledBack);
                }

                var real = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;

                ConsoleOutput.Exception(real, string.Format(ConsoleStrings.RunFailed, sw.ElapsedMilliseconds));
            }
            finally
            {
                failures.Dispose();
                CurrentCommandData = null;

                // What the run showed with Show(...), or cleared, appears on the views.
                ShownGeometry.RefreshIfChanged(RevitAPI.UIDocument);

                if (transaction != null) transaction.Dispose();

                ConsoleOutput.ChunkWritten -= watch;
                if (request.Reported != null)
                {
                    try { request.Reported(report); }
                    catch (Exception ex) { ConsoleOutput.Debug("DevKit: " + ex.Message); }
                }

                if (request.Completed != null)
                {
                    try { request.Completed(failure); }
                    catch (Exception ex) { ConsoleOutput.Exception(ex, ConsoleStrings.CompletedHandlerFailed); }
                }
            }
        }

        private static void ReportResult(object result, long ms)
        {
            if (result == null)
            {
                ConsoleOutput.Success(string.Format(ConsoleStrings.Done, ms));
                return;
            }

            if (result is Result revitResult)
            {
                var level = revitResult == Result.Succeeded ? LogLevel.Success : LogLevel.Warning;
                ConsoleOutput.WriteLine(string.Format(ConsoleStrings.CommandReturned, revitResult, ms), level);
                return;
            }

            ConsoleOutput.WriteLine(string.Format(ConsoleStrings.RunResult, ConsoleOutput.Format(result)), LogLevel.Success);
            ConsoleOutput.Success(string.Format(ConsoleStrings.Done, ms));
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
                    ConsoleOutput.Debug(string.Format(ConsoleStrings.CommandContextFound, candidate));
                    return _commandId;
                }
                catch
                {
                    // invalid id format - try the next candidate
                }
            }

            ConsoleOutput.Debug(ConsoleStrings.RibbonCommandNotFound);
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
                ConsoleOutput.Debug(string.Format(ConsoleStrings.PostCommandFailed, ex.Message));
                return false;
            }
        }
    }
}
