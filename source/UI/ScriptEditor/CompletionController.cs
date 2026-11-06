using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using RevitDevKit.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitDevKit.UI.ScriptEditor
{
    // The completion list of the script editor: opens after '.', on the first letter of a name, or on Ctrl+Space.
    // Only members available in AvalonEdit 4.3 are used, as Revit may have that one loaded (see EditorFactory).
    internal sealed class CompletionController
    {
        private static int _warmed;

        private readonly TextEditor _editor;
        private CompletionWindow _window;
        private CancellationTokenSource _pending;

        private CompletionController(TextEditor editor)
        {
            _editor = editor;
            editor.TextArea.TextEntering += OnTextEntering;
            editor.TextArea.TextEntered += OnTextEntered;
            editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        }

        public static void Attach(TextEditor editor)
        {
            new CompletionController(editor);

            if (Interlocked.Exchange(ref _warmed, 1) == 0)
                Task.Run(() => Safe(ScriptCompletion.Warm));
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.Control)
                return;

            e.Handled = true;
            Request(true);
        }

        private void OnTextEntered(object sender, TextCompositionEventArgs e)
        {
            if (_window != null || e.Text.Length != 1)
                return;

            var c = e.Text[0];
            var caret = _editor.TextArea.Caret.Offset;
            var firstLetter = (char.IsLetter(c) || c == '_') && (caret < 2 || !IsIdentifierChar(_editor.Document.GetCharAt(caret - 2)));

            if (c == '.' || firstLetter)
                Request(false);
        }

        // '.' and '(' accept the selected name as in Visual Studio; any other character just closes the list,
        // so typing a new name does not get replaced by a suggestion.
        private void OnTextEntering(object sender, TextCompositionEventArgs e)
        {
            if (_window == null || e.Text.Length == 0 || IsIdentifierChar(e.Text[0]))
                return;

            if ((e.Text[0] == '.' || e.Text[0] == '(') && SelectionMatchesTypedText())
                _window.CompletionList.RequestInsertion(e);
            else
                _window.Close();
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

        private async void Request(bool explicitRequest)
        {
            _pending?.Cancel();
            var pending = _pending = new CancellationTokenSource();

            var document = _editor.Document;
            var caret = _editor.TextArea.Caret.Offset;
            var line = document.GetLineByOffset(caret);
            var code = document.Text;
            var lineNumber = line.LineNumber;
            var column = caret - line.Offset;

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
            _editor.Dispatcher.BeginInvoke(new Action(() => Show(list, pending, line)));
        }

        private void Show(ScriptCompletionList list, CancellationTokenSource pending, DocumentLine line)
        {
            if (pending != _pending || list == null || _window != null)
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

            ShowWindow(list.Entries, wordStart, typed);
        }

        private void ShowWindow(IList<CompletionEntry> entries, int wordStart, string typed)
        {
            var window = new CompletionWindow(_editor.TextArea) { Width = 320 };
            window.StartOffset = wordStart;

            var data = window.CompletionList.CompletionData;
            foreach (var entry in entries)
                data.Add(new CompletionItem(entry));

            window.Closed += (s, e) =>
            {
                if (_window == window)
                    _window = null;
            };

            _window = window;
            window.Show();

            if (typed.Length > 0)
                window.CompletionList.SelectItem(typed);
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

        public CompletionItem(CompletionEntry entry) => _entry = entry;

        public ImageSource Image => CompletionIcons.For(_entry.Kind);
        public string Text => _entry.Name;
        public object Content => _entry.Name;
        public object Description => _entry.Description;
        public double Priority => 0;

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
