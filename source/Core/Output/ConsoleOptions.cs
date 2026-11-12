using System;

namespace RevitDevKit.Core.Output
{
    // The console's options, one set for every console: the one at the bottom of Script Editor and the dockable
    // pane show the same output, so they show it the same way, and runs from Script Editor and Add-in Explorer
    // alike follow the run options. A change in one console shows in the other at once.
    public static class ConsoleOptions
    {
        private static bool _clearOnRun = true;
        private static bool _hideWarnings;
        private static bool _autoScroll = true;
        private static bool _timestamps;
        private static bool _showDebug = true;

        // The name of the option that changed.
        public static event Action<string> Changed;

        // The console is cleared before each run.
        public static bool ClearOnRun { get => _clearOnRun; set => Set(ref _clearOnRun, value, nameof(ClearOnRun)); }

        // Revit's warnings during a run only go to the console, without Revit's dialog.
        public static bool HideWarnings { get => _hideWarnings; set => Set(ref _hideWarnings, value, nameof(HideWarnings)); }

        // The console follows new output.
        public static bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value, nameof(AutoScroll)); }

        // New lines start with the time.
        public static bool Timestamps { get => _timestamps; set => Set(ref _timestamps, value, nameof(Timestamps)); }

        // Debug lines, call stacks among them, are shown.
        public static bool ShowDebug { get => _showDebug; set => Set(ref _showDebug, value, nameof(ShowDebug)); }

        private static void Set(ref bool field, bool value, string name)
        {
            if (field == value) return;
            field = value;
            Changed?.Invoke(name);
        }
    }
}
