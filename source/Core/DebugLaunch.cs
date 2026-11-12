using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace RevitDevKit.Core
{
    // "Run with debugger". An open Visual Studio is attached to Revit through its automation (DTE), as
    // Debug > Attach to Process does: no dialog and no stop in DevKit's code. With several open, the one whose
    // solution holds the DLL or the script is taken. Without one, Windows asks which debugger to start
    // (Debugger.Launch), and that one stops where it attached: Continue (F5) goes on.
    public static class DebugLaunch
    {
        // Tests answer for the debugger; the argument is the DLL or the script being run.
        public static Func<string, bool> Attach { get; set; } = AttachDebugger;

        public static bool TryAttach(string hintPath = null)
        {
            bool attached;
            try
            {
                attached = Attach(hintPath);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Отладчик: " + ex.Message);
                attached = false;
            }

            if (!attached)
                ConsoleOutput.Warning("Отладчик не подключён: запуск без отладки.");

            return attached;
        }

        private static bool AttachDebugger(string hintPath)
        {
            if (Debugger.IsAttached) return true;

            var studio = VisualStudio.Pick(hintPath);
            if (studio != null && VisualStudio.AttachTo(studio.Item1, Process.GetCurrentProcess().Id))
            {
                // The attach is done in Visual Studio's process; the runtime sees it a moment later.
                for (var i = 0; i < 100 && !Debugger.IsAttached; i++) Thread.Sleep(100);
                if (Debugger.IsAttached)
                {
                    ConsoleOutput.Info("Подключена Visual Studio" + (string.IsNullOrEmpty(studio.Item2) ? "" : " с " + Path.GetFileName(studio.Item2)) + ".");
                    return true;
                }
            }

            ConsoleOutput.Info("Открытая Visual Studio не найдена: выберите отладчик в окне Windows. Он остановится в коде DevKit, продолжите (F5).");
            return Debugger.Launch() && Debugger.IsAttached;
        }

        // Visual Studio's automation, late bound: DevKit references none of its assemblies.
        internal static class VisualStudio
        {
            [DllImport("ole32.dll")]
            private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

            [DllImport("ole32.dll")]
            private static extern int CreateBindCtx(int reserved, out IBindCtx context);

            // Every running Visual Studio with its solution file, in the order Windows lists them.
            public static List<Tuple<object, string>> Running()
            {
                var result = new List<Tuple<object, string>>();
                if (GetRunningObjectTable(0, out var table) != 0 || table == null) return result;

                table.EnumRunning(out var monikers);
                var one = new IMoniker[1];
                while (monikers.Next(1, one, IntPtr.Zero) == 0)
                {
                    try
                    {
                        CreateBindCtx(0, out var context);
                        one[0].GetDisplayName(context, null, out var name);
                        if (name == null || !name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase)) continue;

                        table.GetObject(one[0], out var dte);
                        if (dte == null) continue;

                        string solution = null;
                        try { solution = Get(Get(dte, "Solution"), "FullName") as string; }
                        catch { }

                        result.Add(Tuple.Create(dte, solution));
                    }
                    catch
                    {
                        // A Visual Studio busy or closing: not this one.
                    }
                }

                return result;
            }

            // The one whose solution folder holds the path, the deepest first; else the first not debugging already.
            public static Tuple<object, string> Pick(string hintPath) => Choose(hintPath, Running(), IsDebugging);

            internal static Tuple<object, string> Choose(string hintPath, IEnumerable<Tuple<object, string>> studios, Func<object, bool> isDebugging)
            {
                var running = studios.Where(s => !isDebugging(s.Item1)).ToList();
                if (running.Count == 0) return null;

                if (!string.IsNullOrEmpty(hintPath))
                {
                    var match = running
                        .Where(s => !string.IsNullOrEmpty(s.Item2))
                        .Select(s => new { Studio = s, Folder = Path.GetDirectoryName(s.Item2) })
                        .Where(s => s.Folder != null && hintPath.StartsWith(s.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.Folder.Length)
                        .Select(s => s.Studio)
                        .FirstOrDefault();
                    if (match != null) return match;
                }

                return running[0];
            }

            // dbgDesignMode = 1: not debugging anything now.
            private static bool IsDebugging(object dte)
            {
                try { return Convert.ToInt32(Get(Get(dte, "Debugger"), "CurrentMode")) != 1; }
                catch { return true; }
            }

            public static bool AttachTo(object dte, int processId)
            {
                // Visual Studio may reject a call while it is busy: a few tries.
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        var processes = Get(Get(dte, "Debugger"), "LocalProcesses");
                        var count = Convert.ToInt32(Get(processes, "Count"));
                        for (var i = 1; i <= count; i++)
                        {
                            var process = Call(processes, "Item", i);
                            if (Convert.ToInt32(Get(process, "ProcessID")) != processId) continue;

                            // Managed code only: the engine of the runtime Revit runs on.
                            var engine = Environment.Version.Major >= 5 ? "Managed (.NET Core, .NET 5+)" : "Managed (.NET Framework 4.x)";
                            try { Call(process, "Attach2", engine); }
                            catch { Call(process, "Attach"); }
                            return true;
                        }

                        return false;
                    }
                    catch (COMException)
                    {
                        Thread.Sleep(300);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                    {
                        Thread.Sleep(300);
                    }
                }

                return false;
            }

            private static object Get(object target, string name) =>
                target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

            private static object Call(object target, string name, params object[] args) =>
                target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }
    }
}
