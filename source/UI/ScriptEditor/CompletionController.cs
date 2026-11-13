using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using RevitDevKit.Core.IntelliSense;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitDevKit.UI.ScriptEditor
{
    // IntelliSense of the script editor. The completion list opens after '.', on the first letter of a name, or on Ctrl+Space.
    // After '(' and ',' and a space after them (or on Ctrl+Shift+Space) the overloads of the call show above the line, and when variables
    // of the argument's type are in scope, the completion list opens below with them on top.
    // After '<' of a generic (All<, walls.OfType<) the '<' gets its '>' and the list holds the types that fit.
    // Only members available in AvalonEdit 4.3 are used, as Revit may have that one loaded (see EditorFactory).
    internal sealed class CompletionController
    {
        private static int _warmed;

        private readonly TextEditor _editor;
        private readonly BracketController _brackets;
        private CompletionWindow _window;
        private OverloadInsightWindow _insight;
        private CancellationTokenSource _pending;
        private CancellationTokenSource _pendingSignatures;

        private CompletionController(TextEditor editor)
        {
            _editor = editor;
            editor.TextArea.TextEntering += (s, e) => EditorGuard.Run(() => OnTextEntering(e));
            editor.TextArea.TextEntered += (s, e) => EditorGuard.Run(() => OnTextEntered(e));
            editor.TextArea.PreviewKeyDown += (s, e) => EditorGuard.Run(() => OnPreviewKeyDown(e));

            // After the handlers above, so an open list accepts its name before a closing bracket is stepped over.
            _brackets = BracketController.Attach(editor);
        }

        public static void Attach(TextEditor editor)
        {
            new CompletionController(editor);

            if (Interlocked.Exchange(ref _warmed, 1) == 0)
                Task.Run(() => Safe(ScriptCompletion.Warm));
        }

        // No lists while a box is selected (Alt + drag): a name chosen from one would go into one line of the box only.
        private bool InBoxSelection => BracketController.IsBoxSelection(_editor.TextArea);

        private void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key != Key.Space || InBoxSelection)
                return;

            if (Keyboard.Modifiers == ModifierKeys.Control)
                Request(true);
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                RequestSignatures();
            else
                return;

            e.Handled = true;
        }

        private void OnTextEntered(TextCompositionEventArgs e)
        {
            if (e.Text.Length != 1 || InBoxSelection)
                return;

            var c = e.Text[0];
            // ',' shows the overloads only, the list waits for the space after it: "Show(wall, ".
            if (c == ',')
            {
                RequestSignatures(withList: false);
                return;
            }

            if (c == '(' || c == ' ' && AfterArgumentStart())
            {
                RequestSignatures();
                return;
            }

            if (c == ')')
                _insight?.Close();

            if (_window != null)
                return;

            // Whether '<' opens a generic or compares is known only after binding the name before it.
            if (c == '<')
            {
                Request(true, afterAngle: true);
                return;
            }

            var caret = _editor.TextArea.Caret.Offset;
            var firstLetter = (char.IsLetter(c) || c == '_') && (caret < 2 || !IsIdentifierChar(_editor.Document.GetCharAt(caret - 2)));

            if (c == '.' || firstLetter)
                Request(false);
        }

        // '.', '(', '<' and '>' accept the selected name as in Visual Studio; any other character just closes the list,
        // so typing a new name does not get replaced by a suggestion.
        private void OnTextEntering(TextCompositionEventArgs e)
        {
            if (_window == null || e.Text.Length == 0 || IsIdentifierChar(e.Text[0]))
                return;

            if (".(<>".IndexOf(e.Text[0]) >= 0 && SelectionMatchesTypedText())
                _window.CompletionList.RequestInsertion(e);
            else
                _window.Close();
        }

        // The caret is after ',' or '(' and spaces only.
        private bool AfterArgumentStart()
        {
            var document = _editor.Document;
            var offset = _editor.TextArea.Caret.Offset - 1;
            while (offset >= 0 && document.GetCharAt(offset) == ' ')
                offset--;

            return offset >= 0 && (document.GetCharAt(offset) == ',' || document.GetCharAt(offset) == '(');
        }

        private bool SelectionMatchesTypedText()
        {
            var selected = _window.CompletionList.SelectedItem;
            var typedLength = _window.EndOffset - _window.StartOffset;
            if (selected == null || typedLength <= 0)
                return false;

            var typed = _editor.Document.GetText(_window.StartOffset, typedLength);
            return selected.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
        }

        private async void Request(bool explicitRequest, bool afterAngle = false)
        {
            _pending?.Cancel();
            var pending = _pending = new CancellationTokenSource();

            var document = _editor.Document;
            var caret = _editor.TextArea.Caret.Offset;
            var line = document.GetLineByOffset(caret);
            var code = document.Text;
            var lineNumber = line.LineNumber;
            var column = caret - line.Offset;
            // var watch = Stopwatch.StartNew();

            ScriptCompletionList list;
            try
            {
                list = await Task.Run(() => ScriptCompletion.Get(code, lineNumber, column, explicitRequest, pending.Token), pending.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Подсказки недоступны: " + ex.Message);
                return;
            }

            // Not every caller has a WPF synchronization context, so return to the editor's thread explicitly.
            _editor.Dispatcher.BeginInvoke(new Action(() => EditorGuard.Run(() => Show(list, pending, line, afterAngle))));
        }

        private void Show(ScriptCompletionList list, CancellationTokenSource pending, DocumentLine line, bool afterAngle)
        {
            // A box may have been selected while the list was being looked up.
            if (pending != _pending || list == null || _window != null || InBoxSelection)
                return;

            // After '<' only a generic gets a list: for a comparison the request returns the plain scope.
            if (afterAngle && !list.IsTypeArguments)
                return;

            // The user may have kept typing: show the list only while the caret is still in the same name.
            var document = _editor.Document;
            var wordStart = line.Offset + list.WordStart;
            var caretNow = _editor.TextArea.Caret.Offset;
            if (line.IsDeleted || caretNow < wordStart || caretNow > line.EndOffset)
                return;

            var typed = document.GetText(wordStart, caretNow - wordStart);
            foreach (var c in typed)
            {
                if (!IsIdentifierChar(c))
                    return;
            }

            if (afterAngle)
            {
                var next = caretNow < document.TextLength ? document.GetCharAt(caretNow) : '\n';
                if (next != '>' && (next == '(' || BracketController.ClosesHere(next)))
                    _brackets.Close('>');
            }

            ShowWindow(list.Entries, wordStart, typed, list.Suggestions.Count > 0 ? list.Suggestions : null);

            // Speed check, turned off. To turn it back on, uncomment the watch in Request, pass it here as a Stopwatch watch
            // parameter, and uncomment the timing in ScriptCompletion.Get. Only slow lists were logged, so the debug console
            // did not fill up on every keystroke.
            // if (watch.ElapsedMilliseconds > 150)
            //     ConsoleOutput.Debug("Подсказки открылись за " + watch.ElapsedMilliseconds + " мс (" + list.Entries.Count + " шт.; " + list.Timing + ")");
        }

        // Suggested names go first and are preferred when typed letters match several items equally.
        private void ShowWindow(IList<CompletionEntry> entries, int wordStart, string typed, IList<string> suggested = null)
        {
            var window = new CompletionWindow(_editor.TextArea) { Width = 320 };
            window.StartOffset = wordStart;

            var data = window.CompletionList.CompletionData;
            if (suggested != null)
            {
                foreach (var name in suggested)
                {
                    var entry = entries.FirstOrDefault(e => e.Name == name)
                                ?? new CompletionEntry { Name = name, Kind = CompletionKind.Local };
                    data.Add(new CompletionItem(entry, 1));
                }
            }

            foreach (var entry in entries)
            {
                if (suggested == null || !suggested.Contains(entry.Name))
                    data.Add(new CompletionItem(entry, 0));
            }

            window.Closed += (s, e) =>
            {
                if (_window == window)
                    _window = null;
            };

            _window = window;
            window.Show();

            if (typed.Length > 0)
                window.CompletionList.SelectItem(typed);
            else if (suggested != null && data.Count > 0)
                window.CompletionList.SelectedItem = data[0];
        }

        private async void RequestSignatures(bool withList = true)
        {
            _pendingSignatures?.Cancel();
            var pending = _pendingSignatures = new CancellationTokenSource();

            var document = _editor.Document;
            var caret = _editor.TextArea.Caret.Offset;
            var line = document.GetLineByOffset(caret);
            var code = document.Text;
            var lineNumber = line.LineNumber;
            var column = caret - line.Offset;

            ScriptSignatures signatures;
            ScriptCompletionList list;
            try
            {
                (signatures, list) = await Task.Run(() =>
                {
                    var found = ScriptSignatureHelp.Get(code, lineNumber, column, pending.Token);
                    var scope = withList && found != null && found.Suggestions.Count > 0
                        ? ScriptCompletion.Get(code, lineNumber, column, true, pending.Token)
                        : null;
                    return (found, scope);
                }, pending.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Подсказка параметров недоступна: " + ex.Message);
                return;
            }

            _editor.Dispatcher.BeginInvoke(new Action(() => EditorGuard.Run(() => ShowSignatures(signatures, list, pending))));
        }

        private void ShowSignatures(ScriptSignatures signatures, ScriptCompletionList list, CancellationTokenSource pending)
        {
            if (pending != _pendingSignatures || InBoxSelection)
                return;

            // ',' outside of a call, or the call is gone already.
            if (signatures == null)
            {
                _insight?.Close();
                return;
            }

            int start, end;
            try
            {
                start = _editor.Document.GetOffset(signatures.StartLine, signatures.StartColumn + 1);
                end = Math.Max(_editor.Document.GetOffset(signatures.EndLine, signatures.EndColumn + 1), _editor.TextArea.Caret.Offset);
            }
            catch (ArgumentOutOfRangeException)
            {
                return; // the text changed under the request
            }

            var caret = _editor.TextArea.Caret.Offset;
            if (caret < start || caret > end)
                return;

            var provider = new SignatureProvider(signatures);
            if (_insight != null)
            {
                // Still the same call: the overload picked with the arrows stays while it takes this argument.
                if (_insight.StartOffset == start && _insight.Provider is SignatureProvider old && old.PickedByUser &&
                    old.Count == provider.Count && provider.Takes(old.SelectedIndex, signatures.ArgumentIndex))
                    provider.SelectedIndex = old.SelectedIndex;

                _insight.Provider = provider;
                _insight.StartOffset = start;
                _insight.EndOffset = end;
            }
            else
            {
                var insight = new OverloadInsightWindow(_editor.TextArea) { Provider = provider, StartOffset = start, EndOffset = end };
                insight.Closed += (s, e) =>
                {
                    if (_insight == insight)
                        _insight = null;
                };

                _insight = insight;
                insight.Show();
                KeepAboveLine(insight);
            }

            // Opened after the overloads, so the list gets Up/Down first while both are shown.
            if (list != null && _window == null)
                ShowWindow(list.Entries, caret, string.Empty, signatures.Suggestions);
        }

        // The overloads sit above the caret line, as in Visual Studio, so they do not cover the completion list below it.
        // AvalonEdit places them below and moves them back on scrolling, hence the handlers.
        private void KeepAboveLine(Window window)
        {
            void Place()
            {
                var textView = _editor.TextArea.TextView;
                var source = PresentationSource.FromVisual(textView);
                if (source?.CompositionTarget == null || !window.IsVisible)
                    return;

                var lineTop = textView.GetVisualPosition(_editor.TextArea.Caret.Position, VisualYPosition.LineTop) - textView.ScrollOffset;
                var screen = source.CompositionTarget.TransformFromDevice.Transform(textView.PointToScreen(lineTop));
                var top = screen.Y - window.ActualHeight;
                if (top >= SystemParameters.VirtualScreenTop && Math.Abs(window.Top - top) > 0.5)
                    window.Top = top;
            }

            window.SizeChanged += (s, e) => EditorGuard.Run(Place);
            window.LocationChanged += (s, e) => EditorGuard.Run(Place);
            Place();
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Подсказки: не удалось заранее загрузить сборки: " + ex.Message);
            }
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    internal sealed class CompletionItem : ICompletionData
    {
        private readonly CompletionEntry _entry;

        public CompletionItem(CompletionEntry entry, double priority)
        {
            _entry = entry;
            Priority = priority;
        }

        public ImageSource Image => CompletionIcons.For(_entry.Kind);
        public string Text => _entry.Name;
        public object Content => _entry.Name;
        public object Description => _entry.Description;
        public double Priority { get; }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, _entry.Name);
        }
    }

    // A colored letter per kind, as in Visual Studio's list but drawn here to avoid image resources.
    internal static class CompletionIcons
    {
        private static readonly Dictionary<CompletionKind, ImageSource> Cache = new Dictionary<CompletionKind, ImageSource>();

        public static ImageSource For(CompletionKind kind)
        {
            if (kind == CompletionKind.Keyword)
                return null;

            if (!Cache.TryGetValue(kind, out var image))
                Cache[kind] = image = Draw(kind);

            return image;
        }

        private static ImageSource Draw(CompletionKind kind)
        {
            Glyph(kind, out var letter, out var color);

            var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                letter.Length > 1 ? 7 : 10, Brushes.White, 1);

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(new Rect(0, 0, 16, 16), 3, 3)));
            group.Children.Add(new GeometryDrawing(Brushes.White, null,
                text.BuildGeometry(new Point((16 - text.Width) / 2, (16 - text.Height) / 2))));

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        private static void Glyph(CompletionKind kind, out string letter, out Color color)
        {
            switch (kind)
            {
                case CompletionKind.Namespace: letter = "N"; color = Rgb(0x7F, 0x8C, 0x8D); break;
                case CompletionKind.Class: letter = "C"; color = Rgb(0xE6, 0x7E, 0x22); break;
                case CompletionKind.Struct: letter = "S"; color = Rgb(0xCA, 0x6F, 0x1E); break;
                case CompletionKind.Interface: letter = "I"; color = Rgb(0x34, 0x98, 0xDB); break;
                case CompletionKind.Enum: letter = "E"; color = Rgb(0xB7, 0x95, 0x0B); break;
                case CompletionKind.EnumMember: letter = "e"; color = Rgb(0xB7, 0x95, 0x0B); break;
                case CompletionKind.Delegate: letter = "D"; color = Rgb(0xA9, 0x32, 0x26); break;
                case CompletionKind.Method: letter = "M"; color = Rgb(0x8E, 0x44, 0xAD); break;
                case CompletionKind.Property: letter = "P"; color = Rgb(0x2E, 0x86, 0xC1); break;
                case CompletionKind.Field: letter = "F"; color = Rgb(0x17, 0xA5, 0x89); break;
                case CompletionKind.Event: letter = "Ev"; color = Rgb(0xD6, 0x89, 0x10); break;
                default: letter = "L"; color = Rgb(0x5D, 0x6D, 0x7E); break;
            }
        }

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    }
}
