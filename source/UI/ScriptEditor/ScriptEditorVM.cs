using BIMPlugins.ExtStorage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using RevitDevKit.Scripting;
using RevitDevKit.UI.Console;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace RevitDevKit.UI.ScriptEditor
{
    public partial class ScriptEditorVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        [ObservableProperty] private bool _isAutoTransaction = false;
        [ObservableProperty] private bool _isClearOnRun = true;

        [ObservableProperty] private ScriptTabVM _selectedTab;

        [ObservableProperty] private string _txtStatus = "Готово";
        [ObservableProperty] private string _contextText;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand))]
        private bool _isRunning;

        private int _untitledCounter;

        public ObservableCollection<ScriptTabVM> Tabs { get; } = [];

        public List<string> CodeSamples { get; } = Samples.All.Select(s => s.Title).ToList();

        public ConsoleVM Output { get; } = new ConsoleVM();

        public ScriptEditorVM()
        {
            Output.StatusReported += text => TxtStatus = text;
            NewTab(Samples.Starter, "Новый скрипт");
        }

        public void RefreshContext()
        {
            try
            {
                var doc = RevitAPI.Document;
                ContextText = doc == null ? "Документ не открыт" : "Документ: " + doc.Title;
            }
            catch (Exception ex)
            {
                ContextText = "Контекст недоступен";
                ConsoleOutput.Debug("Не удалось прочитать контекст Revit: " + ex.Message);
            }
        }

        [RelayCommand]
        private void NewTab() => NewTab(Samples.Starter, "Скрипт " + (_untitledCounter + 1));

        [RelayCommand]
        private void OpenFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "C# файлы (*.cs;*.csx)|*.cs;*.csx|Все файлы (*.*)|*.*",
                InitialDirectory = ScriptsFolder()
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var text = File.ReadAllText(dialog.FileName);
                NewTab(text, Path.GetFileName(dialog.FileName), dialog.FileName);

                TxtStatus = "Открыт " + dialog.FileName;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось открыть файл:");
            }
        }

        [RelayCommand]
        private void SaveFile()
        {
            var tab = SelectedTab;
            if (tab == null)
                return;

            var path = tab.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "C# файлы (*.cs)|*.cs|Все файлы (*.*)|*.*",
                    InitialDirectory = ScriptsFolder(),
                    FileName = tab.Title + ".cs"
                };

                if (dialog.ShowDialog() != true)
                    return;

                path = dialog.FileName;
            }

            try
            {
                File.WriteAllText(path, tab.Document.Text);
                tab.FilePath = path;
                tab.Title = Path.GetFileName(path);
                tab.IsDirty = false;

                TxtStatus = "Сохранено: " + path;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось сохранить файл:");
            }
        }

        [RelayCommand]
        private void SampleSelected(string value)
        {
            var sample = Samples.All.FirstOrDefault(s => s.Title == value);
            if (sample == null)
                return;

            var tab = Tabs.FirstOrDefault(t => t.FilePath == null && t.Title == value);
            if (tab == null)
                NewTab(sample.Code, value);
            else
                SelectedTab = tab;
        }

        [RelayCommand]
        private void CloseTab(ScriptTabVM tab)
        {
            tab ??= SelectedTab;
            if (tab == null) return;

            if (tab.IsDirty)
            {
                var answer = MessageBox.Show(
                    "Во вкладке «" + tab.Title + "» есть несохранённые изменения. Закрыть?",
                    "Script Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            var index = Tabs.IndexOf(tab);
            Tabs.Remove(tab);

            if (Tabs.Count == 0)
                NewTab(Samples.Starter, "Новый скрипт");
            else if (SelectedTab == null || SelectedTab == tab)
                SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        [RelayCommand]
        private void RevealInExplorer(ScriptTabVM tab)
        {
            tab ??= SelectedTab;
            if (tab == null || string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath)) return;
            Process.Start("explorer.exe", "/select,\"" + tab.FilePath + "\"");
        }

        private ScriptTabVM NewTab(string code, string title, string filePath = null)
        {
            _untitledCounter++;

            var tab = new ScriptTabVM(code, title, filePath);
            Tabs.Add(tab);
            SelectedTab = tab;

            return tab;
        }

        private static string ScriptsFolder()
        {
            var dir = Path.Combine(App.DataDirectory, "Scripts");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private bool CanRun() => !IsRunning;

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task RunAsync()
        {
            var tab = SelectedTab;
            if (tab == null) return;

            var code = tab.Document.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                TxtStatus = "Пустой скрипт";
                return;
            }

            if (IsClearOnRun) ConsoleOutput.Clear();

            IsRunning = true;
            TxtStatus = "Компиляция…";

            var title = tab.Title;
            var autoTransaction = IsAutoTransaction;

            // An exception escaping an async command would reach Revit's dispatcher - catch it here.
            CompileResult result;
            try
            {
                result = await Task.Run(() => ScriptCompiler.Compile(code));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Сбой компилятора:");
                Finish("Ошибка компиляции");
                return;
            }

            foreach (var message in result.Messages)
            {
                var level = message.IsError ? LogLevel.Error : LogLevel.Warning;
                ConsoleOutput.WriteLine(message.ToString(), level);
            }

            if (!result.Success)
            {
                var count = result.Errors.Count();
                ConsoleOutput.Error("Компиляция не удалась: " + count + (count == 1 ? " ошибка." : " ошибок."));
                Finish("Ошибка компиляции");

                var first = result.Errors.FirstOrDefault(m => m.Line > 0);
                if (first != null) tab.NavigateTo(first.Line, first.Column);
                return;
            }

            var target = EntryPointResolver.ResolveScriptEntry(result.Assembly);
            if (target == null)
            {
                ConsoleOutput.Error("В коде не нашлось точки входа: нужен класс с IExternalCommand либо метод Main / Run / Execute.");
                Finish("Нет точки входа");
                return;
            }

            ConsoleOutput.Debug("Скомпилировано за " + result.ElapsedMs + " мс → " + target.Details);
            TxtStatus = "Выполнение…";

            var request = new RunRequest
            {
                Title = title,
                AutoTransaction = autoTransaction,
                TransactionName = "DevKit: " + title,
                Body = () => target.Invoke(),
                Completed = error => _dispatcher.Invoke(() =>
                {
                    Finish(error == null ? "Готово" : "Завершено с ошибкой");
                    RefreshContext();
                })
            };

            try
            {
                RevitTaskRunner.Run(request, target.Kind == RunTargetKind.ExternalCommand);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось передать скрипт в Revit:");
                Finish("Ошибка запуска");
            }
        }

        private void Finish(string status)
        {
            IsRunning = false;
            TxtStatus = status;
        }
    }

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
