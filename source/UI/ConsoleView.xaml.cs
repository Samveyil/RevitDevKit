using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using RevitScriptRunner.Core;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Консоль плагина: показывает всё, что пишется в ConsoleOutput. Вывод идёт
    /// в FlowDocument пачками — это забота View, настройки берутся из ConsoleVM.
    /// </summary>
    public partial class ConsoleView : UserControl
    {
        private readonly Queue<LogChunkEventArgs> _pending = new Queue<LogChunkEventArgs>();
        private readonly object _pendingSync = new object();
        private readonly DispatcherTimer _flushTimer;
        private readonly Paragraph _paragraph;

        private bool _atLineStart = true;
        private bool _subscribed;

        /// <summary>
        /// Не отписываться от вывода, когда контрол уходит из дерева. Нужно панели Revit:
        /// скрытая панель тоже должна копить вывод, а живёт она весь сеанс.
        /// </summary>
        public static readonly DependencyProperty IsPersistentProperty = DependencyProperty.Register(
            nameof(IsPersistent), typeof(bool), typeof(ConsoleView), new PropertyMetadata(false));

        public bool IsPersistent
        {
            get => (bool)GetValue(IsPersistentProperty);
            set => SetValue(IsPersistentProperty, value);
        }

        public ConsoleView()
        {
            InitializeComponent();

            // Абзац один на всю консоль: строки добавляются как Run + LineBreak.
            // Нулевые отступы — это не оформление, а условие того, чтобы вывод
            // выглядел потоком текста, а не набором абзацев документа.
            _paragraph = new Paragraph { Margin = new Thickness(0) };
            Output.Document = new FlowDocument(_paragraph) { PagePadding = new Thickness(2) };

            // Вывод из скрипта может идти тысячами строк в секунду — копим и рисуем пачками.
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _flushTimer.Tick += (s, e) => Flush();

            // Подписываемся сразу, а не по Loaded: редактор кода пишет диагностику
            // ещё при загрузке окна.
            Subscribe();
            Loaded += (s, e) => Subscribe();
            Unloaded += (s, e) =>
            {
                if (!IsPersistent) Unsubscribe();
            };
        }

        private ConsoleVM Options => DataContext as ConsoleVM;

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;

            ConsoleOutput.ChunkWritten += OnChunkWritten;
            ConsoleOutput.Cleared += OnCleared;
            _flushTimer.Start();
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;

            ConsoleOutput.ChunkWritten -= OnChunkWritten;
            ConsoleOutput.Cleared -= OnCleared;
            _flushTimer.Stop();
        }

        private void OnChunkWritten(object sender, LogChunkEventArgs e)
        {
            lock (_pendingSync) _pending.Enqueue(e);
        }

        private void OnCleared(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnCleared(sender, e)));
                return;
            }

            lock (_pendingSync) _pending.Clear();
            _paragraph.Inlines.Clear();
            _atLineStart = true;
        }

        private void Flush()
        {
            LogChunkEventArgs[] batch;
            lock (_pendingSync)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            var options = Options;
            var showDebug = options?.IsShowDebug ?? true;
            var showTime = options?.IsTimestamps ?? false;

            foreach (var chunk in batch)
            {
                if (chunk.Level == LogLevel.Debug && !showDebug) continue;
                Append(chunk, showTime);
            }

            Trim();

            if (options?.IsAutoScroll ?? true) Output.ScrollToEnd();
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
                    _paragraph.Inlines.Add(
                        new Run(chunk.Timestamp.ToString("HH:mm:ss.fff") + "  ") { Foreground = Brushes.Gray });
                    _atLineStart = false;
                }

                if (text.Length > 0)
                {
                    var run = new Run(text);
                    if (brush != null) run.Foreground = brush;
                    _paragraph.Inlines.Add(run);
                    _atLineStart = false;
                }

                if (i < parts.Length - 1)
                {
                    _paragraph.Inlines.Add(new LineBreak());
                    _atLineStart = true;
                }
            }
        }

        private void Trim()
        {
            const int limit = 12000;
            const int drop = 4000;

            if (_paragraph.Inlines.Count <= limit) return;

            var toRemove = _paragraph.Inlines.Take(drop).ToList();
            foreach (var inline in toRemove) _paragraph.Inlines.Remove(inline);

            _paragraph.Inlines.InsertBefore(
                _paragraph.Inlines.FirstInline,
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
        private void OnCopy(object sender, RoutedEventArgs e)
        {
            var range = new TextRange(Output.Document.ContentStart, Output.Document.ContentEnd);
            try
            {
                Clipboard.SetText(range.Text);
                Options?.ReportStatus("Вывод скопирован в буфер обмена");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Буфер обмена занят:");
            }
        }
    }
}
