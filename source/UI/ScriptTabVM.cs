using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;

namespace RevitDevKit.UI
{
    public partial class ScriptTabVM : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        private string _title;

        [ObservableProperty] private string _filePath;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        private bool _isDirty;

        public TextDocument Document { get; }

        public string Header { get => IsDirty ? Title + " *" : Title; }

        public event Action<int, int> NavigateRequested;

        public ScriptTabVM(string code, string title, string filePath = null)
        {
            Document = new TextDocument { Text = code ?? string.Empty };
            _title = title;
            _filePath = filePath;

            Document.TextChanged += (s, e) => IsDirty = true;
        }

        public void NavigateTo(int line, int column)
        {
            line = Math.Max(1, Math.Min(line, Document.LineCount));
            NavigateRequested?.Invoke(line, Math.Max(1, column));
        }
    }
}
