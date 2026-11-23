using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Show;
using System;
using RevitDevKit.Localization;

namespace RevitDevKit.UI.Console
{
    // A console's view model. Its options are ConsoleOptions, the same for every console: a change in one shows in
    // the others at once.
    public partial class ConsoleVM : ObservableObject
    {
        public ConsoleVM()
        {
            ConsoleOptions.Changed += name =>
            {
                if (name != nameof(ConsoleOptions.OpenPane))
                {
                    OnPropertyChanged("Is" + name);
                    return;
                }

                OnPropertyChanged(nameof(OpenPaneText));
                OnPropertyChanged(nameof(OpenPaneIcon));
                OnPropertyChanged(nameof(OpenPaneToolTip));
                OnPropertyChanged(nameof(IsOpenOnProblems));
                OnPropertyChanged(nameof(IsOpenOnOutput));
                OnPropertyChanged(nameof(IsOpenAlways));
                OnPropertyChanged(nameof(IsOpenNever));
            };
        }

        // When a run from Add-in Explorer or Repeat Last opens the console pane: one radio button each, and the
        // choice shown on the menu's button.
        public bool IsOpenOnProblems { get => Is(ConsolePaneOpening.OnProblems); set => Pick(ConsolePaneOpening.OnProblems, value); }
        public bool IsOpenOnOutput { get => Is(ConsolePaneOpening.OnOutput); set => Pick(ConsolePaneOpening.OnOutput, value); }
        public bool IsOpenAlways { get => Is(ConsolePaneOpening.Always); set => Pick(ConsolePaneOpening.Always, value); }
        public bool IsOpenNever { get => Is(ConsolePaneOpening.Never); set => Pick(ConsolePaneOpening.Never, value); }

        public string OpenPaneText
        {
            get
            {
                switch (ConsoleOptions.OpenPane)
                {
                    case ConsolePaneOpening.OnOutput: return ConsoleStrings.OpenPaneOnOutput;
                    case ConsolePaneOpening.Always: return ConsoleStrings.OpenPaneAlways;
                    case ConsolePaneOpening.Never: return ConsoleStrings.OpenPaneNever;
                    default: return ConsoleStrings.OpenPaneOnProblems;
                }
            }
        }

        // The bell on the menu's button: with "!" on problems, ringing on any output, plain always, crossed out never.
        public MaterialDesignThemes.Wpf.PackIconKind OpenPaneIcon
        {
            get
            {
                switch (ConsoleOptions.OpenPane)
                {
                    case ConsolePaneOpening.OnOutput: return MaterialDesignThemes.Wpf.PackIconKind.BellRingOutline;
                    case ConsolePaneOpening.Always: return MaterialDesignThemes.Wpf.PackIconKind.BellOutline;
                    case ConsolePaneOpening.Never: return MaterialDesignThemes.Wpf.PackIconKind.BellOffOutline;
                    default: return MaterialDesignThemes.Wpf.PackIconKind.BellAlertOutline;
                }
            }
        }

        public string OpenPaneToolTip => string.Format(ConsoleStrings.OpenPaneToolTip, OpenPaneText);

        private static bool Is(ConsolePaneOpening opening) => ConsoleOptions.OpenPane == opening;

        private static void Pick(ConsolePaneOpening opening, bool value)
        {
            if (value) ConsoleOptions.OpenPane = opening;
        }

        // What runs from Script Editor and Add-in Explorer do with the console.
        public bool IsClearOnRun { get => ConsoleOptions.ClearOnRun; set => ConsoleOptions.ClearOnRun = value; }
        public bool IsClearShownOnRun { get => ConsoleOptions.ClearShownOnRun; set => ConsoleOptions.ClearShownOnRun = value; }
        public bool IsHideWarnings { get => ConsoleOptions.HideWarnings; set => ConsoleOptions.HideWarnings = value; }

        // How the console shows its output.
        public bool IsAutoScroll { get => ConsoleOptions.AutoScroll; set => ConsoleOptions.AutoScroll = value; }
        public bool IsTimestamps { get => ConsoleOptions.Timestamps; set => ConsoleOptions.Timestamps = value; }
        public bool IsShowDebug { get => ConsoleOptions.ShowDebug; set => ConsoleOptions.ShowDebug = value; }

        public event Action<string> StatusReported;

        [RelayCommand]
        private void Clear() => ConsoleOutput.Clear();

        // Takes away what scripts drew with Show(...) and add-ins with Debug.WriteLine(geometry).
        [RelayCommand]
        private void ClearShown()
        {
            ShownGeometry.Clear();
            ShownGeometry.RequestRefresh();
        }

        public void ReportStatus(string text) => StatusReported?.Invoke(text);
    }
}
