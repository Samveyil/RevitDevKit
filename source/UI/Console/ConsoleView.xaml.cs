using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace RevitDevKit.UI.Console
{
    public partial class ConsoleView : UserControl
    {
        private readonly Queue<LogChunkEventArgs> _pending = new Queue<LogChunkEventArgs>();
        private readonly object _pendingSync = new object();
        private readonly DispatcherTimer _flushTimer;
        private readonly Paragraph _paragraph;

        private bool _atLineStart = true;
        private bool _subscribed;

        // Lines never wrap: the document is made as wide as the longest line (Consolas is
        // monospaced, so characters are enough), and a wide table scrolls horizontally instead.
        private int _lineLength;
        private int _longestLine;
        private double _charWidth;

        // The dockable pane is hidden rather than destroyed: keep collecting output while hidden.
        public static readonly DependencyProperty IsPersistentProperty = DependencyProperty.Register(
            nameof(IsPersistent), typeof(bool), typeof(ConsoleView), new PropertyMetadata(false));

        public bool IsPersistent
        {
            get => (bool)GetValue(IsPersistentProperty);
            set => SetValue(IsPersistentProperty, value);
        }

        // The console options of how output is shown go into the Вид menu instead of checkboxes: Script Editor's
        // console is narrower than the pane, and its header has to stay one row.
        public static readonly DependencyProperty IsViewMenuProperty = DependencyProperty.Register(
            nameof(IsViewMenu), typeof(bool), typeof(ConsoleView), new PropertyMetadata(false, (d, e) =>
            {
                var view = (ConsoleView)d;
                var menu = (bool)e.NewValue;
                view.ViewMenu.Visibility = menu ? Visibility.Visible : Visibility.Collapsed;
                view.ViewInline.Visibility = menu ? Visibility.Collapsed : Visibility.Visible;
            }));

        public bool IsViewMenu
        {
            get => (bool)GetValue(IsViewMenuProperty);
            set => SetValue(IsViewMenuProperty, value);
        }

        // Characters a line of wrappable output may take before it wraps; -1: as many as the console shows now;
        // 0: never wrapped.
        public static readonly DependencyProperty WrapWidthProperty = DependencyProperty.Register(
            nameof(WrapWidth), typeof(int), typeof(ConsoleView), new PropertyMetadata(0));

        public int WrapWidth
        {
            get => (int)GetValue(WrapWidthProperty);
            set => SetValue(WrapWidthProperty, value);
        }

        public ConsoleView()
        {
            InitializeComponent();

            _paragraph = new Paragraph { Margin = new Thickness(0) };
            Output.Document = new FlowDocument(_paragraph) { PagePadding = new Thickness(2) };

            // Scripts can print thousands of lines per second: queue output and render in batches.
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _flushTimer.Tick += (s, e) => Flush();

            Subscribe();
            Loaded += (s, e) =>
            {
                Subscribe();
                UpdatePageWidth();
            };
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
            _lineLength = 0;
            _longestLine = 0;
            UpdatePageWidth();
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
            UpdatePageWidth();

            if (options?.IsAutoScroll ?? true) Output.ScrollToEnd();
        }

        private void EnsureCharWidth()
        {
            if (_charWidth > 0) return;

            var sample = new FormattedText("0", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Output.FontFamily, Output.FontStyle, Output.FontWeight, Output.FontStretch),
                Output.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _charWidth = sample.WidthIncludingTrailingWhitespace;
        }

        // The columns wrappable output may take: WrapWidth, or with -1 what fits in the console's visible width
        // now, less the time stamp when it is shown. Output already written is not wrapped again on a resize.
        private int WrapColumns(bool showTime)
        {
            if (WrapWidth >= 0) return WrapWidth;

            EnsureCharWidth();
            var padding = Output.Document.PagePadding;
            var width = Output.ActualWidth - Output.Padding.Left - Output.Padding.Right
                        - Output.BorderThickness.Left - Output.BorderThickness.Right
                        - padding.Left - padding.Right - SystemParameters.VerticalScrollBarWidth - _charWidth;
            if (width <= 0 || _charWidth <= 0) return 100;

            return Math.Max(40, (int)(width / _charWidth) - (showTime ? 14 : 0));
        }

        private void UpdatePageWidth()
        {
            EnsureCharWidth();

            var padding = Output.Document.PagePadding;
            var longest = Math.Max(_longestLine, _lineLength);
            Output.Document.PageWidth = longest * _charWidth + padding.Left + padding.Right + _charWidth;
        }

        private void Append(LogChunkEventArgs chunk, bool showTime)
        {
            var brush = BrushFor(chunk.Level);
            var parts = chunk.Text.Replace("\r\n", "\n").Split('\n');
            var columns = chunk.Wrappable ? WrapColumns(showTime) : 0;
            if (columns > 0)
            {
                // The last part is what follows the final line break: kept as it is.
                parts = parts.Take(parts.Length - 1).SelectMany(p => ConsoleText.Wrap(p, columns))
                    .Concat(new[] { parts[parts.Length - 1] }).ToArray();
            }

            for (var i = 0; i < parts.Length; i++)
            {
                var text = parts[i];

                if (_atLineStart && showTime && (text.Length > 0 || i < parts.Length - 1))
                {
                    var stamp = chunk.Timestamp.ToString("HH:mm:ss.fff") + "  ";
                    _paragraph.Inlines.Add(new Run(stamp) { Foreground = Brushes.Gray });
                    _lineLength += stamp.Length;
                    _atLineStart = false;
                }

                if (text.Length > 0)
                {
                    var run = new Run(text);
                    if (brush != null) run.Foreground = brush;
                    _paragraph.Inlines.Add(run);
                    _lineLength += text.Length;
                    _atLineStart = false;
                }

                if (i < parts.Length - 1)
                {
                    _paragraph.Inlines.Add(new LineBreak());
                    _longestLine = Math.Max(_longestLine, _lineLength);
                    _lineLength = 0;
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
