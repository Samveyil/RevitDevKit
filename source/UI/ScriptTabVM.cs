using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Вкладка со скриптом. Текст хранится в TextDocument — это модель AvalonEdit,
    /// а не контрол: история отмены живёт в ней же, так что её можно держать во VM,
    /// а сам редактор создаёт View (см. ScriptEditorHost).
    /// </summary>
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

        /// <summary>Просьба к редактору поставить каретку (строка, столбец — с единицы).</summary>
        public event Action<int, int> NavigateRequested;

        public ScriptTabVM(string code, string title, string filePath = null)
        {
            // Текст задаём до подписки: свежая вкладка не считается изменённой.
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