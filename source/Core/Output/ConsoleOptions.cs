using System;

namespace RevitDevKit.Core.Output
{
    // When a run from Add-in Explorer or Repeat Last opens the console pane.
    public enum ConsolePaneOpening
    {
        // It failed, or Revit or the command wrote a warning or an error.
        OnProblems,

        // As OnProblems, or the command wrote anything at all.
        OnOutput,

        Always,

        // Only the Console button opens it.
        Never
    }

    // The console's options, one set for every console: the one at the bottom of Script Editor and the dockable
    // pane show the same output, so they show it the same way, and runs from Script Editor and Add-in Explorer
    // alike follow the run options. A change in one console shows in the other at once.
    public static class ConsoleOptions
    {
        private static bool _clearOnRun = true;
        private static bool _clearShownOnRun = true;
        private static bool _hideWarnings;
        private static bool _autoScroll = true;
        private static bool _timestamps;
        private static bool _showDebug = true;
        private static ConsolePaneOpening _openPane = ConsolePaneOpening.OnProblems;

        // The name of the option that changed.
        public static event Action<string> Changed;

        // The console is cleared before each run.
        public static bool ClearOnRun { get => _clearOnRun; set => Set(ref _clearOnRun, value, nameof(ClearOnRun)); }

        // What Show(...) and Debug.WriteLine(geometry) drew is cleared before each run. Off, it stays to compare with
        // the next run, which draws in the next colours of the palette.
        public static bool ClearShownOnRun { get => _clearShownOnRun; set => Set(ref _clearShownOnRun, value, nameof(ClearShownOnRun)); }

        // Revit's warnings during a run only go to the console, without Revit's dialog.
        public static bool HideWarnings { get => _hideWarnings; set => Set(ref _hideWarnings, value, nameof(HideWarnings)); }

        // The console follows new output.
        public static bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value, nameof(AutoScroll)); }

        // New lines start with the time.
        public static bool Timestamps { get => _timestamps; set => Set(ref _timestamps, value, nameof(Timestamps)); }

        // Debug lines, call stacks among them, are shown.
        public static bool ShowDebug { get => _showDebug; set => Set(ref _showDebug, value, nameof(ShowDebug)); }

        // When a run from Add-in Explorer or Repeat Last opens the console pane. Script Editor has its own console.
        public static ConsolePaneOpening OpenPane
        {
            get => _openPane;
            set
            {
                if (_openPane == value) return;
                _openPane = value;
                Changed?.Invoke(nameof(OpenPane));
            }
        }

        // Whether a finished run opens the console pane under OpenPane.
        public static bool ShouldOpenPane(bool failed, bool hasProblems, bool hasOutput)
        {
            switch (_openPane)
            {
                case ConsolePaneOpening.Always: return true;
                case ConsolePaneOpening.Never: return false;
                case ConsolePaneOpening.OnOutput: return failed || hasProblems || hasOutput;
                default: return failed || hasProblems;
            }
        }

        private static void Set(ref bool field, bool value, string name)
        {
            if (field == value) return;
            field = value;
            Changed?.Invoke(name);
        }
    }
}
