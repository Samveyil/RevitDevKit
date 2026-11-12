using System;
using System.Diagnostics;

namespace RevitDevKit.Core
{
    // "Run with debugger": Windows asks which debugger to attach, an open Visual Studio among them,
    // and the run goes on under it. A DLL command's symbols load with its copy, so breakpoints set in
    // the project bind; a script stops on its first line.
    public static class DebugLaunch
    {
        // Tests answer for the debugger.
        public static Func<bool> Attach { get; set; } = AttachDebugger;

        public static bool TryAttach()
        {
            bool attached;
            try
            {
                attached = Attach();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Debugger.Launch: " + ex.Message);
                attached = false;
            }

            if (!attached)
                ConsoleOutput.Warning("Отладчик не подключён: запуск без отладки.");

            return attached;
        }

        private static bool AttachDebugger() => Debugger.IsAttached || Debugger.Launch() && Debugger.IsAttached;
    }
}
