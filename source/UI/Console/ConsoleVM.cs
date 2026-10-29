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

        public event Action<string> StatusReported;

        [RelayCommand]
        private void Clear() => ConsoleOutput.Clear();

        public void ReportStatus(string text) => StatusReported?.Invoke(text);
    }
}
