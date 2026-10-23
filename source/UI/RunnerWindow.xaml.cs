using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RevitScriptRunner.Core;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Логика окна — в RunnerVM. Здесь остались жизненный цикл окна и отрисовка
    /// консоли: вывод идёт в FlowDocument пачками, и это чисто забота View.
    /// </summary>
    public partial class RunnerWindow : Window
    {
        private static RunnerWindow _instance;

        private readonly RunnerVM _vm;
        private readonly Queue<LogChunkEventArgs> _pending = new Queue<LogChunkEventArgs>();
        private readonly object _pendingSync = new object();

        private DispatcherTimer _flushTimer;
        private Paragraph _consoleParagraph;
        private bool _atLineStart = true;

        // ------------------------------------------------------------------
        //  Жизненный цикл окна
        // ------------------------------------------------------------------

        public RunnerWindow(RunnerVM vm)
        {
            InitializeComponent();

            _vm = vm;
            DataContext = vm;

            SetupConsole();

            // Подписываемся до показа окна: редактор первой вкладки создаётся при
            // загрузке и пишет в консоль диагностику по версии AvalonEdit.
            ConsoleOutput.ChunkWritten += OnChunkWritten;
            ConsoleOutput.Cleared += OnConsoleCleared;

            Closed += OnWindowClosed;
        }

        /// <summary>Окно одно на сеанс Revit: повторное нажатие кнопки просто поднимает его.</summary>
        public static void ShowSingleton()
        {
            if (_instance != null)
            {
                _instance._vm.RefreshContext();
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new RunnerWindow(new RunnerVM());

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

            _instance._vm.RefreshContext();
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

            var showDebug = _vm.IsShowDebug;
            var showTime = _vm.IsTimestamps;

            foreach (var chunk in batch)
            {
                if (chunk.Level == LogLevel.Debug && !showDebug) continue;
                Append(chunk, showTime);
            }

            TrimConsole();

            if (_vm.IsAutoScroll) Console.ScrollToEnd();
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

        /// <summary>Текст консоли есть только во View, поэтому копирование — здесь.</summary>
        private void OnCopyConsole(object sender, RoutedEventArgs e)
        {
            var range = new TextRange(Console.Document.ContentStart, Console.Document.ContentEnd);
            try
            {
                Clipboard.SetText(range.Text);
                _vm.TxtStatus = "Вывод скопирован в буфер обмена";
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Буфер обмена занят:");
            }
        }
    }
}
