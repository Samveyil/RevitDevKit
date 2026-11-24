using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using RevitDevKit.Core;
using RevitDevKit.Core.Ai;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Running;
using RevitDevKit.ScriptApi;
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
using RevitDevKit.Localization;

namespace RevitDevKit.UI.ScriptEditor
{
    public partial class ScriptEditorVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        // Kept between sessions in DevKitSettings.
        [ObservableProperty] private bool _isAutoTransaction = DevKitSettings.AutoTransaction;

        partial void OnIsAutoTransactionChanged(bool value) => DevKitSettings.AutoTransaction = value;

        // Undo and Redo of the toolbar and of Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z: the current tab's own history, the one
        // the editor keeps, so the buttons and the keys undo the same steps. Available while there is something to undo.
        private bool CanUndo() => SelectedTab?.Document.UndoStack.CanUndo == true;
        private bool CanRedo() => SelectedTab?.Document.UndoStack.CanRedo == true;

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo() => SelectedTab.Document.UndoStack.Undo();

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo() => SelectedTab.Document.UndoStack.Redo();

        partial void OnSelectedTabChanged(ScriptTabVM oldValue, ScriptTabVM newValue)
        {
            if (oldValue != null) oldValue.Document.UndoStack.PropertyChanged -= OnUndoStackChanged;
            if (newValue != null) newValue.Document.UndoStack.PropertyChanged += OnUndoStackChanged;
            OnUndoStackChanged(null, null);
        }

        private void OnUndoStackChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }

        [ObservableProperty] private ScriptTabVM _selectedTab;

        [ObservableProperty] private string _txtStatus = ScriptEditorStrings.Ready;
        [ObservableProperty] private string _contextText;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand))]
        [NotifyCanExecuteChangedFor(nameof(RunWithDebuggerCommand))]
        private bool _isRunning;

        private int _untitledCounter;

        public ObservableCollection<ScriptTabVM> Tabs { get; } = [];

        public List<string> CodeSamples { get; } = Samples.All.Select(s => s.Title).ToList();

        public ConsoleVM Output { get; } = new ConsoleVM();

        public ScriptEditorVM()
        {
            Output.StatusReported += text => TxtStatus = text;
            NewTab(Samples.Starter, ScriptEditorStrings.NewScript);
        }

        public void RefreshContext()
        {
            try
            {
                var doc = RevitAPI.Document;
                ContextText = doc == null ? ScriptEditorStrings.NoDocument : string.Format(ScriptEditorStrings.DocumentContext, doc.Title);
            }
            catch (Exception ex)
            {
                ContextText = ScriptEditorStrings.ContextUnavailable;
                ConsoleOutput.Debug(string.Format(ScriptEditorStrings.ContextReadFailed, ex.Message));
            }
        }

        [RelayCommand]
        private void NewTab() => NewTab(Samples.Starter, string.Format(ScriptEditorStrings.ScriptNumber, _untitledCounter + 1));

        [RelayCommand]
        private void OpenFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = ScriptEditorStrings.OpenFilter,
                InitialDirectory = ScriptsFolder()
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var text = File.ReadAllText(dialog.FileName);
                NewTab(text, Path.GetFileName(dialog.FileName), dialog.FileName);

                TxtStatus = string.Format(ScriptEditorStrings.Opened, dialog.FileName);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ScriptEditorStrings.OpenFailed);
            }
        }

        [RelayCommand]
        private void SaveFile()
        {
            var tab = SelectedTab;
            if (tab == null)
                return;

            var path = tab.FilePath.IsNullOrEmpty() ? AskPath(tab) : tab.FilePath;
            if (path != null)
                Save(tab, path);
        }

        // Always asks where, starting from the tab's own file: a copy under another name, the tab then follows it.
        [RelayCommand]
        private void SaveFileAs(ScriptTabVM tab)
        {
            tab ??= SelectedTab;
            if (tab == null)
                return;

            var path = AskPath(tab);
            if (path != null)
                Save(tab, path);
        }

        // Tests answer the dialog themselves.
        internal Func<ScriptTabVM, string> AskPath { get; set; } = AskPathInDialog;

        private static string AskPathInDialog(ScriptTabVM tab)
        {
            var hasFile = !tab.FilePath.IsNullOrEmpty();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ScriptEditorStrings.SaveFilter,
                InitialDirectory = hasFile ? Path.GetDirectoryName(tab.FilePath) : ScriptsFolder(),
                FileName = hasFile ? Path.GetFileName(tab.FilePath) : tab.Title + ".cs"
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private void Save(ScriptTabVM tab, string path)
        {
            try
            {
                File.WriteAllText(path, tab.Document.Text);
                tab.FilePath = path;
                tab.Title = Path.GetFileName(path);
                tab.Document.UndoStack.MarkAsOriginalFile();
                tab.IsDirty = false;

                TxtStatus = string.Format(ScriptEditorStrings.Saved, path);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ScriptEditorStrings.SaveFailed);
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
                    string.Format(ScriptEditorStrings.UnsavedChanges, tab.Title),
                    "Script Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            var index = Tabs.IndexOf(tab);
            Tabs.Remove(tab);

            if (Tabs.Count == 0)
                NewTab(Samples.Starter, ScriptEditorStrings.NewScript);
            else if (SelectedTab == null || SelectedTab == tab)
                SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        [RelayCommand]
        private void RevealInExplorer(ScriptTabVM tab)
        {
            tab ??= SelectedTab;
            if (tab == null || tab.FilePath.IsNullOrEmpty() || !File.Exists(tab.FilePath)) return;
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
        private Task RunAsync() => RunCoreAsync(debug: false);

        // Asks to attach a debugger first; the script then stops on its first line.
        [RelayCommand(CanExecute = nameof(CanRun))]
        private Task RunWithDebuggerAsync() => RunCoreAsync(debug: true);

        // The file the debugger shows: the tab's own when it is saved as it is, otherwise a snapshot of the text.
        private static string DebugFile(ScriptTabVM tab, string code)
        {
            if (tab.HasFile && !tab.IsDirty && File.Exists(tab.FilePath))
                return tab.FilePath;

            var dir = Path.Combine(Path.GetTempPath(), "RevitDevKit", "Debug");
            Directory.CreateDirectory(dir);
            var name = string.Concat((tab.Title ?? ScriptEditorStrings.DefaultScriptName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name += ".cs";

            var path = Path.Combine(dir, name);
            File.WriteAllText(path, code, System.Text.Encoding.UTF8);
            return path;
        }

        private async Task RunCoreAsync(bool debug)
        {
            var tab = SelectedTab;
            if (tab == null) return;

            var code = tab.Document.Text;
            if (code.IsNullOrWhiteSpace())
            {
                TxtStatus = ScriptEditorStrings.EmptyScript;
                return;
            }

            if (ConsoleOptions.ClearOnRun) ConsoleOutput.Clear();

            // Without a debugger nothing runs: the user has been told to open Visual Studio.
            string debugFile = null;
            if (debug)
            {
                if (!DebugLaunch.TryAttach(tab.FilePath))
                {
                    TxtStatus = ScriptEditorStrings.DebuggerNotAttached;
                    return;
                }

                debugFile = DebugFile(tab, code);
                ConsoleOutput.Info(string.Format(ScriptEditorStrings.DebugStop, debugFile));
            }

            IsRunning = true;
            TxtStatus = ScriptEditorStrings.Compiling;

            var title = tab.Title;
            var autoTransaction = IsAutoTransaction;

            // An exception escaping an async command would reach Revit's dispatcher - catch it here.
            CompileResult result;
            try
            {
                result = await Task.Run(() => ScriptCompiler.Compile(code, debugFile));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ScriptEditorStrings.CompilerCrashed);
                Finish(ScriptEditorStrings.CompileError);
                return;
            }

            // What went wrong is kept with the tab for the AI instructions with the script (CopyAiInstructionsWithScript).
            tab.LastProblems.Clear();
            foreach (var message in result.Messages)
            {
                var level = message.IsError ? LogLevel.Error : LogLevel.Warning;
                ConsoleOutput.WriteLine(message.ToString(), level);
                tab.LastProblems.Add(message.ToString());
            }

            if (!result.Success)
            {
                var count = result.Errors.Count();
                ConsoleOutput.Error(string.Format(ScriptEditorStrings.CompileFailed, Language.Count(count, ScriptEditorStrings.ErrorsOne, ScriptEditorStrings.ErrorsFew, ScriptEditorStrings.ErrorsMany)));
                Finish(ScriptEditorStrings.CompileError);

                var first = result.Errors.FirstOrDefault(m => m.Line > 0);
                if (first != null) tab.NavigateTo(first.Line, first.Column);
                return;
            }

            var target = EntryPointResolver.ResolveScriptEntry(result.Assembly);
            if (target == null)
            {
                ConsoleOutput.Error(ScriptEditorStrings.NoEntryPoint);
                tab.LastProblems.Add(ScriptEditorStrings.NoEntryPointProblem);
                Finish(ScriptEditorStrings.NoEntryPointStatus);
                return;
            }

            ConsoleOutput.Debug(string.Format(ScriptEditorStrings.Compiled, result.ElapsedMs, target.Details));
            TxtStatus = ScriptEditorStrings.Running;

            var request = new RunRequest
            {
                Title = title,
                AutoTransaction = autoTransaction,
                HideWarnings = ConsoleOptions.HideWarnings,
                TransactionName = "DevKit: " + title,
                Body = () => target.Invoke(),
                Completed = error => _dispatcher.Invoke(() =>
                {
                    if (error != null) tab.LastProblems.Add(DescribeFailure(error));
                    Finish(error == null ? ScriptEditorStrings.Done : ScriptEditorStrings.FinishedWithError);
                    RefreshContext();
                })
            };

            try
            {
                RevitTaskRunner.Run(request, target.Kind == RunTargetKind.ExternalCommand);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ScriptEditorStrings.HandOverFailed);
                Finish(ScriptEditorStrings.RunError);
            }
        }

        private void Finish(string status)
        {
            IsRunning = false;
            TxtStatus = status;
        }

        // "InvalidOperationException: ... at the script's lines": the exception the script threw, not its wrappers.
        private static string DescribeFailure(Exception error)
        {
            while ((error is System.Reflection.TargetInvocationException || error is AggregateException) && error.InnerException != null)
                error = error.InnerException;

            var inScript = (error.StackTrace ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("RevitDevKit.UserScripts"))
                .Select(l => l.Trim())
                .Take(3)
                .ToList();

            return string.Format(ScriptEditorStrings.RunException, error.GetType().Name, error.Message)
                   + (inScript.Count == 0 ? string.Empty : " (" + string.Join("; ", inScript) + ")");
        }

        // The AI button repeats the copy chosen last from its menu; kept while Revit runs, for every Script Editor window.
        private static bool _lastAiWithScript;

        public bool IsLastAiWithScript
        {
            get => _lastAiWithScript;
            private set
            {
                if (_lastAiWithScript == value) return;
                _lastAiWithScript = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AiButtonIcon));
                OnPropertyChanged(nameof(AiButtonToolTip));
            }
        }

        // The detailed instructions: all of BIMPlugins.ExtStorage with signatures, about three times the tokens.
        // Kept like the last choice, between sessions too (DevKitSettings).
        public bool IsAiDetailed
        {
            get => DevKitSettings.AiDetailed;
            set
            {
                if (DevKitSettings.AiDetailed == value) return;
                DevKitSettings.AiDetailed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AiButtonToolTip));
            }
        }

        public MaterialDesignThemes.Wpf.PackIconKind AiButtonIcon =>
            IsLastAiWithScript ? MaterialDesignThemes.Wpf.PackIconKind.AutoFix : MaterialDesignThemes.Wpf.PackIconKind.CreationOutline;

        public string AiButtonToolTip =>
            (IsLastAiWithScript ? ScriptEditorStrings.AiCopyScript : ScriptEditorStrings.AiCopy) +
            (IsAiDetailed ? ScriptEditorStrings.AiDetailedSuffix : "");

        [RelayCommand]
        private void RepeatAiCopy()
        {
            if (IsLastAiWithScript) CopyAiInstructionsWithScript();
            else CopyAiInstructions();
        }

        // Instructions for an AI chat on writing scripts for Script Editor, copied to paste at the start of a chat.
        [RelayCommand]
        private void CopyAiInstructions()
        {
            IsLastAiWithScript = false;
            CopyToClipboard(AiInstructions.Build(IsAutoTransaction, IsAiDetailed), ScriptEditorStrings.AiCopied);
        }

        // The same with the current script and what went wrong in its last run, to ask the AI to fix or change it.
        [RelayCommand]
        private void CopyAiInstructionsWithScript()
        {
            IsLastAiWithScript = true;
            var tab = SelectedTab;
            if (tab == null) return;

            CopyToClipboard(AiInstructions.BuildWithScript(IsAutoTransaction, tab.Title, tab.Document.Text, tab.LastProblems, IsAiDetailed),
                ScriptEditorStrings.AiScriptCopied);
        }

        private void CopyToClipboard(string text, string status)
        {
            try
            {
                Clipboard.SetText(text);
                TxtStatus = status;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning(string.Format(ScriptEditorStrings.ClipboardFailed, ex.Message));
            }
        }
    }

    public partial class ScriptTabVM : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        private string _title;

        // Set once the tab is opened from a file or saved to one; a new tab and a sample have none.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFile))]
        private string _filePath;

        // Only such a tab has a file to show in Explorer.
        public bool HasFile => !FilePath.IsNullOrEmpty();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        private bool _isDirty;

        public TextDocument Document { get; }

        // Compile errors and warnings of the tab's last run, and the exception it threw; for the AI instructions.
        public List<string> LastProblems { get; } = new List<string>();

        public string Header { get => IsDirty ? Title + " *" : Title; }

        public event Action<int, int> NavigateRequested;

        public ScriptTabVM(string code, string title, string filePath = null)
        {
            // Through the constructor, so the starting text is not a step Undo would take back to an empty tab.
            Document = new TextDocument(code ?? string.Empty);
            _title = title;
            _filePath = filePath;

            // Changed while the text differs from the saved one (or the starting one): undoing every edit takes the mark off.
            Document.UndoStack.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                    IsDirty = !Document.UndoStack.IsOriginalFile;
            };
        }

        public void NavigateTo(int line, int column)
        {
            line = Math.Max(1, Math.Min(line, Document.LineCount));
            NavigateRequested?.Invoke(line, Math.Max(1, column));
        }
    }
}
