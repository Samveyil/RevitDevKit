using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;

namespace RevitDevKit.UI
{
    /// <summary>
    /// Настройки консоли плагина. Сам вывод общий для всех окон (ConsoleOutput),
    /// а рисует его ConsoleView.
    /// </summary>
    public partial class ConsoleVM : ObservableObject
    {
        [ObservableProperty] private bool _isAutoScroll = true;
        [ObservableProperty] private bool _isTimestamps = false;
        [ObservableProperty] private bool _isShowDebug = true;

        /// <summary>Сообщение для строки состояния окна, в котором стоит консоль.</summary>
        public event Action<string> StatusReported;

        [RelayCommand]
        private void Clear() => ConsoleOutput.Clear();

        public void ReportStatus(string text) => StatusReported?.Invoke(text);
    }
}