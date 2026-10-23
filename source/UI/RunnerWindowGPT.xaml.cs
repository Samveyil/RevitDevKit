using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using ICSharpCode.AvalonEdit;
using RevitScriptRunner.Core;
using RevitScriptRunner.Scripting;

namespace RevitScriptRunner.UI
{
    public partial class RunnerWindowGPT : Window
    {
        private static RunnerWindowGPT _instance;

        private readonly List<ScriptTab> _tabs = new List<ScriptTab>();
        private readonly List<LoadedAssembly> _assemblies = new List<LoadedAssembly>();
        private readonly Queue<LogChunkEventArgs> _pending = new Queue<LogChunkEventArgs>();
        private readonly object _pendingSync = new object();

        private DispatcherTimer _flushTimer;
        private Paragraph _consoleParagraph;
        private bool _atLineStart = true;
        private int _untitledCounter;
        private bool _running;

        // ------------------------------------------------------------------
        //  Жизненный цикл окна
        // ------------------------------------------------------------------

        public RunnerWindowGPT()
        {
            InitializeComponent();

            SetupConsole();

            // Подписываемся до создания первой вкладки: редактор при инициализации
            // пишет в консоль диагностику по версии AvalonEdit.
            ConsoleOutput.ChunkWritten += OnChunkWritten;
            ConsoleOutput.Cleared += OnConsoleCleared;

            SetupSamples();
            UpdateUnloadHint();

            NewTab(Samples.Starter, "Новый скрипт");

            PreviewKeyDown += OnWindowKeyDown;
            Closed += OnWindowClosed;
        }

        /// <summary>Окно одно на сеанс Revit: повторное нажатие кнопки просто поднимает его.</summary>
        public static void ShowSingleton()
        {
            if (_instance != null)
            {
                _instance.RefreshContext();
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new RunnerWindowGPT();

            // Привязываем к главному окну Revit, чтобы не терялось за ним.
            try
            {
                var helper = new WindowInteropHelper(_instance);
                helper.Owner = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                // не критично
            }

            _instance.RefreshContext();
            _instance.Show();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null) return;
            try { _instance.Close(); } catch { /* Revit уже закрывается */ }
            _instance = null;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            ConsoleOutput.ChunkWritten -= OnChunkWritten;
            ConsoleOutput.Cleared -= OnConsoleCleared;
            if (_flushTimer != null) _flushTimer.Stop();
            _instance = null;
        }

        private void RefreshContext()
        {
            try
            {
                var doc = RevitAPI.Document;
                TxtContext.Text = doc == null ? "Документ не открыт" : "Документ: " + doc.Title;
            }
            catch (Exception ex)
            {
                TxtContext.Text = "Контекст недоступен";
                ConsoleOutput.Debug("Не удалось прочитать контекст Revit: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Вкладки со скриптами
        // ------------------------------------------------------------------

        private ScriptTab Current
        {
            get { return ScriptTabs.SelectedItem is TabItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null; }
        }

        private ScriptTab NewTab(string code, string title, string filePath = null)
        {
            _untitledCounter++;

            var editor = EditorFactory.Create();
            editor.Text = code ?? string.Empty;

            var item = new TabItem { Content = editor };
            var tab = new ScriptTab { Item = item, Editor = editor, FilePath = filePath, Title = title };

            item.Header = tab.Header;
            item.ContextMenu = BuildTabMenu(tab);

            editor.TextChanged += (s, e) =>
            {
                if (tab.Dirty) return;
                tab.Dirty = true;
                item.Header = tab.Header;
            };

            _tabs.Add(tab);
            ScriptTabs.Items.Add(item);
            ScriptTabs.SelectedItem = item;
            editor.Focus();

            return tab;
        }

        private ContextMenu BuildTabMenu(ScriptTab tab)
        {
            var menu = new ContextMenu();

            var close = new MenuItem { Header = "Закрыть вкладку" };
            close.Click += (s, e) => CloseTab(tab);
            menu.Items.Add(close);

            var reveal = new MenuItem { Header = "Показать в проводнике" };
            reveal.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath)) return;
                Process.Start("explorer.exe", "/select,\"" + tab.FilePath + "\"");
            };
            menu.Items.Add(reveal);

            return menu;
        }

        private void CloseTab(ScriptTab tab)
        {
            if (tab == null) return;

            if (tab.Dirty)
            {
                var answer = MessageBox.Show(this,
                    "Во вкладке «" + tab.Title + "» есть несохранённые изменения. Закрыть?",
                    "Script Runner", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            _tabs.Remove(tab);
            ScriptTabs.Items.Remove(tab.Item);

            if (_tabs.Count == 0) NewTab(Samples.Starter, "Новый скрипт");
        }

        private void OnNewTab(object sender, RoutedEventArgs e)
        {
            NewTab(Samples.Starter, "Скрипт " + (_untitledCounter + 1));
        }

        private void OnOpenFile(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "C# файлы (*.cs;*.csx)|*.cs;*.csx|Все файлы (*.*)|*.*",
                InitialDirectory = ScriptsFolder()
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var text = File.ReadAllText(dialog.FileName);
                var tab = NewTab(text, Path.GetFileName(dialog.FileName), dialog.FileName);
                tab.Dirty = false;
                tab.Item.Header = tab.Header;
                Status("Открыт " + dialog.FileName);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось открыть файл:");
            }
        }

        private void OnSaveFile(object sender, RoutedEventArgs e)
        {
            var tab = Current;
            if (tab == null) return;

            var path = tab.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "C# файлы (*.cs)|*.cs|Все файлы (*.*)|*.*",
                    InitialDirectory = ScriptsFolder(),
                    FileName = tab.Title.TrimEnd('*', ' ') + ".cs"
                };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }

            try
            {
                File.WriteAllText(path, tab.Editor.Text);
                tab.FilePath = path;
                tab.Title = Path.GetFileName(path);
                tab.Dirty = false;
                tab.Item.Header = tab.Header;
                Status("Сохранено: " + path);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось сохранить файл:");
            }
        }

        private static string ScriptsFolder()
        {
            var dir = Path.Combine(App.DataDirectory, "Scripts");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void SetupSamples()
        {
            CmbSamples.ItemsSource = Samples.All.Select(s => s.Title).ToList();
            CmbSamples.SelectedIndex = -1;
        }

        private void OnSampleSelected(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSamples.SelectedIndex < 0) return;

            var sample = Samples.All[CmbSamples.SelectedIndex];
            NewTab(sample.Code, sample.Title);
            CmbSamples.SelectedIndex = -1;
        }

        // ------------------------------------------------------------------
        //  Запуск скрипта
        // ------------------------------------------------------------------

        private void OnRun(object sender, RoutedEventArgs e)
        {
            RunCurrentScript();
        }

        private void RunCurrentScript()
        {
            if (_running) return;

            var tab = Current;
            if (tab == null) return;

            var code = tab.Editor.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                Status("Пустой скрипт");
                return;
            }

            if (ChkClearOnRun.IsChecked == true) ConsoleOutput.Clear();

            SetRunning(true);
            Status("Компиляция…");

            var autoTransaction = ChkAutoTransaction.IsChecked == true;
            var title = tab.Title;

            Task.Run(() => ScriptCompiler.Compile(code))
                .ContinueWith(task => OnCompiled(task, title, autoTransaction),
                    TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnCompiled(Task<CompileResult> task, string title, bool autoTransaction)
        {
            if (task.IsFaulted)
            {
                ConsoleOutput.Exception(task.Exception, "Сбой компилятора:");
                SetRunning(false);
                Status("Ошибка компиляции");
                return;
            }

            var result = task.Result;

            foreach (var message in result.Messages)
            {
                var level = message.IsError ? LogLevel.Error : LogLevel.Warning;
                ConsoleOutput.WriteLine(message.ToString(), level);
            }

            if (!result.Success)
            {
                var count = result.Errors.Count();
                ConsoleOutput.Error("Компиляция не удалась: " + count + (count == 1 ? " ошибка." : " ошибок."));
                SetRunning(false);
                Status("Ошибка компиляции");
                GoToFirstError(result);
                return;
            }

            var target = EntryPointResolver.ResolveScriptEntry(result.Assembly);
            if (target == null)
            {
                ConsoleOutput.Error("В коде не нашлось точки входа: нужен класс с IExternalCommand либо метод Main / Run / Execute.");
                SetRunning(false);
                Status("Нет точки входа");
                return;
            }

            ConsoleOutput.Debug("Скомпилировано за " + result.ElapsedMs + " мс → " + target.Details);
            Status("Выполнение…");

            var request = new RunRequest
            {
                Title = title,
                AutoTransaction = autoTransaction,
                TransactionName = "Script Runner: " + title,
                Body = () => target.Invoke(),
                Completed = error => Dispatcher.Invoke(() =>
                {
                    SetRunning(false);
                    Status(error == null ? "Готово" : "Завершено с ошибкой");
                    RefreshContext();
                })
            };

            RevitTaskRunner.Run(request, target.Kind == RunTargetKind.ExternalCommand);
        }

        private void GoToFirstError(CompileResult result)
        {
            var first = result.Errors.FirstOrDefault(m => m.Line > 0);
            var tab = Current;
            if (first == null || tab == null) return;

            try
            {
                var line = Math.Min(first.Line, tab.Editor.Document.LineCount);
                tab.Editor.TextArea.Caret.Line = line;
                tab.Editor.TextArea.Caret.Column = Math.Max(1, first.Column);
                tab.Editor.ScrollToLine(line);
                tab.Editor.Focus();
            }
            catch
            {
                // позиция не сошлась — не беда
            }
        }

        private void SetRunning(bool running)
        {
            _running = running;
            BtnRun.IsEnabled = !running;
        }

        // ------------------------------------------------------------------
        //  Внешние сборки
        // ------------------------------------------------------------------

        private void OnAddAssembly(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Сборки .NET (*.dll)|*.dll|Все файлы (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) != true) return;

            foreach (var file in dialog.FileNames) AddAssembly(file);
        }

        private void AddAssembly(string path)
        {
            try
            {
                var loaded = ScriptAssemblyLoader.LoadFromFile(path);
                _assemblies.RemoveAll(a => string.Equals(a.OriginalPath, loaded.OriginalPath, StringComparison.OrdinalIgnoreCase));
                _assemblies.Add(loaded);

                var targets = EntryPointResolver.FindAll(loaded.Assembly);
                ConsoleOutput.Success("Загружено: " + loaded.DisplayName + " — точек входа: " + targets.Count);

                RebuildAssemblyTree();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось загрузить " + path + ":");
            }
        }

        private void OnReloadAssembly(object sender, RoutedEventArgs e)
        {
            var loaded = SelectedAssembly();
            if (loaded == null)
            {
                Status("Выберите сборку в списке");
                return;
            }

            var path = loaded.OriginalPath;
            if (ScriptAssemblyLoader.TryUnload(loaded))
                ConsoleOutput.Debug("Предыдущая версия выгружена.");
            else
                ConsoleOutput.Warning("На .NET Framework выгрузка невозможна: старая версия останется в памяти до перезапуска Revit.");

            _assemblies.Remove(loaded);
            AddAssembly(path);
        }

        private void OnRemoveAssembly(object sender, RoutedEventArgs e)
        {
            var loaded = SelectedAssembly();
            if (loaded == null) return;

            ScriptAssemblyLoader.TryUnload(loaded);
            _assemblies.Remove(loaded);
            RebuildAssemblyTree();
            Status("Убрано: " + loaded.DisplayName);
        }

        private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedTarget() != null) OnRunTarget(sender, e);
        }

        private void OnRunTarget(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            var target = SelectedTarget();
            if (target == null)
            {
                Status("Выберите команду или метод в дереве");
                return;
            }

            if (ChkClearOnRun.IsChecked == true) ConsoleOutput.Clear();

            SetRunning(true);
            Status("Выполнение " + target.DisplayName + "…");

            var request = new RunRequest
            {
                Title = target.DisplayName,
                AutoTransaction = target.Kind == RunTargetKind.Method && ChkAutoTransaction.IsChecked == true,
                TransactionName = "Script Runner: " + target.Type.Name,
                Body = () => target.Invoke(),
                Completed = error => Dispatcher.Invoke(() =>
                {
                    SetRunning(false);
                    Status(error == null ? "Готово" : "Завершено с ошибкой");
                })
            };

            RevitTaskRunner.Run(request, target.Kind == RunTargetKind.ExternalCommand);
        }

        private LoadedAssembly SelectedAssembly()
        {
            var item = AssemblyTree.SelectedItem as TreeViewItem;
            while (item != null)
            {
                if (item.Tag is LoadedAssembly loaded) return loaded;
                item = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
            }
            return null;
        }

        private RunTarget SelectedTarget()
        {
            var item = AssemblyTree.SelectedItem as TreeViewItem;
            return item == null ? null : item.Tag as RunTarget;
        }

        private void RebuildAssemblyTree()
        {
            AssemblyTree.Items.Clear();

            foreach (var loaded in _assemblies)
            {
                var root = new TreeViewItem
                {
                    Header = loaded.DisplayName + "   —   " + loaded.LoadedAt.ToString("HH:mm:ss"),
                    Tag = loaded,
                    IsExpanded = true,
                    ToolTip = loaded.OriginalPath
                };

                var targets = EntryPointResolver.FindAll(loaded.Assembly);
                if (targets.Count == 0)
                {
                    root.Items.Add(new TreeViewItem
                    {
                        Header = "— нечего запускать —",
                        Foreground = Brushes.Gray
                    });
                }

                foreach (var target in targets)
                {
                    // Тип точки входа виден из DisplayName, цветом не выделяем.
                    root.Items.Add(new TreeViewItem
                    {
                        Header = target.DisplayName,
                        Tag = target,
                        ToolTip = target.Details
                    });
                }

                AssemblyTree.Items.Add(root);
            }
        }

        private void UpdateUnloadHint()
        {
            TxtUnloadHint.Text = ScriptAssemblyLoader.SupportsUnload
                ? "Выгрузка сборок поддерживается (.NET 8)"
                : "Выгрузка сборок недоступна (.NET Framework)";
        }

        // ------------------------------------------------------------------
        //  Консоль
        // ------------------------------------------------------------------

        private void SetupConsole()
        {
            // Абзац один на всю консоль: строки добавляются как Run + LineBreak.
            // Нулевые отступы — это не оформление, а условие того, чтобы вывод
            // выглядел потоком текста, а не набором абзацев документа.
            _consoleParagraph = new Paragraph { Margin = new Thickness(0) };
            Console.Document = new FlowDocument(_consoleParagraph) { PagePadding = new Thickness(2) };

            // Вывод из скрипта может идти тысячами строк в секунду — копим и рисуем пачками.
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _flushTimer.Tick += (s, e) => FlushConsole();
            _flushTimer.Start();
        }

        private void OnChunkWritten(object sender, LogChunkEventArgs e)
        {
            lock (_pendingSync) _pending.Enqueue(e);
        }

        private void OnConsoleCleared(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnConsoleCleared(sender, e)));
                return;
            }

            lock (_pendingSync) _pending.Clear();
            _consoleParagraph.Inlines.Clear();
            _atLineStart = true;
        }

        private void FlushConsole()
        {
            LogChunkEventArgs[] batch;
            lock (_pendingSync)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            var showDebug = ChkShowDebug.IsChecked == true;
            var showTime = ChkTimestamps.IsChecked == true;

            foreach (var chunk in batch)
            {
                if (chunk.Level == LogLevel.Debug && !showDebug) continue;
                Append(chunk, showTime);
            }

            TrimConsole();

            if (ChkAutoScroll.IsChecked == true) Console.ScrollToEnd();
        }

        private void Append(LogChunkEventArgs chunk, bool showTime)
        {
            var brush = BrushFor(chunk.Level);
            var parts = chunk.Text.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < parts.Length; i++)
            {
                var text = parts[i];

                if (_atLineStart && showTime && (text.Length > 0 || i < parts.Length - 1))
                {
                    _consoleParagraph.Inlines.Add(
                        new Run(chunk.Timestamp.ToString("HH:mm:ss.fff") + "  ") { Foreground = Brushes.Gray });
                    _atLineStart = false;
                }

                if (text.Length > 0)
                {
                    var run = new Run(text);
                    if (brush != null) run.Foreground = brush;
                    _consoleParagraph.Inlines.Add(run);
                    _atLineStart = false;
                }

                if (i < parts.Length - 1)
                {
                    _consoleParagraph.Inlines.Add(new LineBreak());
                    _atLineStart = true;
                }
            }
        }

        private void TrimConsole()
        {
            const int limit = 12000;
            const int drop = 4000;

            if (_consoleParagraph.Inlines.Count <= limit) return;

            var toRemove = _consoleParagraph.Inlines.Take(drop).ToList();
            foreach (var inline in toRemove) _consoleParagraph.Inlines.Remove(inline);

            _consoleParagraph.Inlines.InsertBefore(
                _consoleParagraph.Inlines.FirstInline,
                new Run("… ранний вывод отброшен …" + Environment.NewLine) { Foreground = Brushes.Gray });
        }

        /// <summary>
        /// Цвет уровня сообщения. Это не оформление, а различение ошибки от обычного
        /// вывода; берутся системные цвета, читаемые на стандартном фоне.
        /// Для обычного вывода возвращается null — цвет наследуется от контрола.
        /// </summary>
        private static Brush BrushFor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Success: return Brushes.DarkGreen;
                case LogLevel.Warning: return Brushes.DarkOrange;
                case LogLevel.Error: return Brushes.Firebrick;
                case LogLevel.Debug: return Brushes.Gray;
                case LogLevel.System: return Brushes.DarkBlue;
                default: return null;
            }
        }

        private void OnClearConsole(object sender, RoutedEventArgs e)
        {
            ConsoleOutput.Clear();
        }

        private void OnCopyConsole(object sender, RoutedEventArgs e)
        {
            var range = new TextRange(Console.Document.ContentStart, Console.Document.ContentEnd);
            try
            {
                Clipboard.SetText(range.Text);
                Status("Вывод скопирован в буфер обмена");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Буфер обмена занят:");
            }
        }

        private void Status(string text)
        {
            TxtStatus.Text = text;
        }

        // ------------------------------------------------------------------
        //  Горячие клавиши
        // ------------------------------------------------------------------

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (e.Key == Key.F5 || (ctrl && e.Key == Key.Enter))
            {
                RunCurrentScript();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.S)
            {
                OnSaveFile(sender, e);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.O)
            {
                OnOpenFile(sender, e);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.N)
            {
                OnNewTab(sender, e);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.L)
            {
                ConsoleOutput.Clear();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.W)
            {
                CloseTab(Current);
                e.Handled = true;
            }
        }

        private sealed class ScriptTab
        {
            public TabItem Item;
            public TextEditor Editor;
            public string FilePath;
            public string Title;
            public bool Dirty;

            public string Header
            {
                get { return Dirty ? Title + " *" : Title; }
            }
        }
    }
}
