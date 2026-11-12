using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using System;

namespace RevitDevKit.UI.Console
{
    // A console's view model. Its options are ConsoleOptions, the same for every console: a change in one shows in
    // the others at once.
    public partial class ConsoleVM : ObservableObject
    {
        public ConsoleVM()
        {
            ConsoleOptions.Changed += name => OnPropertyChanged("Is" + name);
        }

        // What runs from Script Editor and Add-in Explorer do with the console.
        public bool IsClearOnRun { get => ConsoleOptions.ClearOnRun; set => ConsoleOptions.ClearOnRun = value; }
        public bool IsHideWarnings { get => ConsoleOptions.HideWarnings; set => ConsoleOptions.HideWarnings = value; }

        // How the console shows its output.
        public bool IsAutoScroll { get => ConsoleOptions.AutoScroll; set => ConsoleOptions.AutoScroll = value; }
        public bool IsTimestamps { get => ConsoleOptions.Timestamps; set => ConsoleOptions.Timestamps = value; }
        public bool IsShowDebug { get => ConsoleOptions.ShowDebug; set => ConsoleOptions.ShowDebug = value; }

        public event Action<string> StatusReported;

        [RelayCommand]
        private void Clear() => ConsoleOutput.Clear();

        public void ReportStatus(string text) => StatusReported?.Invoke(text);
    }
}
