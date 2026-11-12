using System;

namespace RevitDevKit.Core
{
    // How runs from DevKit treat the console, one setting for Script Editor and Add-in Explorer alike: they write
    // to the same console, and its header, in the editor and in the dockable pane, is where these are switched.
    public static class RunOptions
    {
        private static bool _clearOnRun = true;
        private static bool _hideWarnings;

        // The name of the option that changed.
        public static event Action<string> Changed;

        // The console is cleared before each run.
        public static bool ClearOnRun
        {
            get => _clearOnRun;
            set
            {
                if (_clearOnRun == value) return;
                _clearOnRun = value;
                Changed?.Invoke(nameof(ClearOnRun));
            }
        }

        // Revit's warnings during a run only go to the console, without Revit's dialog.
        public static bool HideWarnings
        {
            get => _hideWarnings;
            set
            {
                if (_hideWarnings == value) return;
                _hideWarnings = value;
                Changed?.Invoke(nameof(HideWarnings));
            }
        }
    }
}
