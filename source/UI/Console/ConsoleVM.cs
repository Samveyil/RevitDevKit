using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using System;

namespace RevitDevKit.UI.Console
{
    public partial class ConsoleVM : ObservableObject
    {
        [ObservableProperty] private bool _isAutoScroll = true;
        [ObservableProperty] private bool _isTimestamps = false;
        [ObservableProperty] private bool _isShowDebug = true;

        public ConsoleVM()
        {
            // Both consoles show the same options: a change in one shows in the other.
            RunOptions.Changed += name => OnPropertyChanged(name == nameof(RunOptions.ClearOnRun) ? nameof(IsClearOnRun) : nameof(IsHideWarnings));
        }

        // Options of the runs that write here, shared by Script Editor and Add-in Explorer.
        public bool IsClearOnRun
        {
            get => RunOptions.ClearOnRun;
            set => RunOptions.ClearOnRun = value;
        }

        public bool IsHideWarnings
        {
            get => RunOptions.HideWarnings;
            set => RunOptions.HideWarnings = value;
        }

        public event Action<string> StatusReported;

        [RelayCommand]
        private void Clear() => ConsoleOutput.Clear();

        public void ReportStatus(string text) => StatusReported?.Invoke(text);
    }
}
