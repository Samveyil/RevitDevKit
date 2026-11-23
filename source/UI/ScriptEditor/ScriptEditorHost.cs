using BIMPlugins.ExtStorage.Extensions;
using ICSharpCode.AvalonEdit;
using RevitDevKit.Core.Output;
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RevitDevKit.Localization;

namespace RevitDevKit.UI.ScriptEditor
{
    // TabControl reuses one ContentPresenter for all tabs. Keep one editor per tab
    // so caret, scroll and undo survive tab switches; it lives as long as the tab VM.
    public sealed class ScriptEditorHost : ContentControl
    {
        private static readonly ConditionalWeakTable<ScriptTabVM, TextEditor> Editors =
            new ConditionalWeakTable<ScriptTabVM, TextEditor>();

        public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
            nameof(Tab), typeof(ScriptTabVM), typeof(ScriptEditorHost),
            new PropertyMetadata(null, (d, e) => ((ScriptEditorHost)d).Show((ScriptTabVM)e.NewValue)));

        public ScriptTabVM Tab
        {
            get => (ScriptTabVM)GetValue(TabProperty);
            set => SetValue(TabProperty, value);
        }

        private void Show(ScriptTabVM tab)
        {
            if (tab == null)
            {
                Content = null;
                return;
            }

            var editor = Editors.GetValue(tab, CreateEditor);

            if (editor.Parent is ContentControl owner && owner != this)
                owner.Content = null;

            Content = editor;
            Dispatcher.BeginInvoke(new Action(() => editor.Focus()), DispatcherPriority.Input);
        }

        private static TextEditor CreateEditor(ScriptTabVM tab)
        {
            var editor = EditorFactory.Create();
            editor.Document = tab.Document;
            CompletionController.Attach(editor);
            BoxSelectionController.Attach(editor);
            CommentController.Attach(editor);

            tab.NavigateRequested += (line, column) =>
            {
                try
                {
                    editor.TextArea.Caret.Line = line;
                    editor.TextArea.Caret.Column = column;
                    editor.ScrollToLine(line);
                    editor.Focus();
                }
                catch { } // line/column out of range after an edit
            };

            return editor;
        }
    }

    internal static class EditorFactory
    {
        public static TextEditor Create()
        {
            var editor = new TextEditor
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                ShowLineNumbers = true,
                WordWrap = false,
                SyntaxHighlighting = CSharpHighlighting.Definition
            };

            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;
            editor.TextArea.TextView.BackgroundRenderers.Add(new IndentRainbowRenderer(editor));
            editor.Loaded += (s, e) => PaintScrollCorner(editor);
            SnapScrollToLines(editor);

            editor.TextArea.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control) return;

                Duplicate(editor);
                e.Handled = true;
            };

            return editor;
        }

        // The vertical scroll stops on a line, whatever moves it (wheel, thumb, arrows, the caret): the top line is shown whole.
        // An offset between two lines goes on in the direction of the move, to the next line down or the one above, so a step
        // shorter than half a line (an arrow click with a large font) still moves. At the very end the last line is shown whole
        // instead, as the document may end between two line tops.
        internal static void SnapScrollToLines(TextEditor editor)
        {
            var view = editor.TextArea.TextView;
            var last = 0.0;

            view.ScrollOffsetChanged += (s, e) =>
            {
                var offset = view.VerticalOffset;
                var movedDown = offset > last;
                last = offset;

                // Half a pixel ahead: line tops are sums of fractional heights, and an offset right on one may come out a hair above it.
                var line = view.GetDocumentLineByVisualTop(offset + 0.5);
                if (line == null)
                    return;

                var top = view.GetVisualTopByDocumentLine(line.LineNumber);
                if (Math.Abs(offset - top) < 0.5)
                    return;

                var info = (IScrollInfo)view;
                var bottom = info.ExtentHeight - info.ViewportHeight;
                if (bottom - offset < 0.5)
                    return;

                var next = line.NextLine != null ? view.GetVisualTopByDocumentLine(line.LineNumber + 1) : top;
                var target = movedDown ? Math.Min(next, bottom) : top;
                if (Math.Abs(target - offset) >= 0.5)
                    editor.ScrollToVerticalOffset(target);
            };
        }

        // The square where the editor's two scroll bars meet is a gray rectangle of its ScrollViewer's template, darker
        // than the scroll bar tracks (EditorScrollBars.xaml); it takes the tracks' colour. Missing parts are left alone.
        private static void PaintScrollCorner(TextEditor editor)
        {
            try
            {
                if (!(editor.Template?.FindName("PART_ScrollViewer", editor) is ScrollViewer viewer)) return;
                viewer.ApplyTemplate();
                if (viewer.Template?.FindName("PART_Corner", viewer) is Shape corner && editor.TryFindResource("ScrollTrackBrush") is Brush track)
                    corner.Fill = track;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug(string.Format(ScriptEditorStrings.ScrollCornerFailed, ex.Message));
            }
        }

        // Ctrl+D as in Visual Studio: duplicates the current line, or the selection,
        // as a single undo step.
        internal static void Duplicate(TextEditor editor)
        {
            var document = editor.Document;
            var caret = editor.TextArea.Caret;
            var caretLine = 0;
            var caretColumn = 0;

            document.BeginUpdate();
            try
            {
                if (editor.SelectionLength > 0)
                {
                    var text = editor.SelectedText;
                    var end = editor.SelectionStart + editor.SelectionLength;

                    document.Insert(end, text);
                    editor.Select(end, text.Length);
                }
                else
                {
                    var line = document.GetLineByOffset(caret.Offset);
                    caretLine = line.LineNumber + 1;
                    caretColumn = caret.Column;

                    var delimited = line.DelimiterLength > 0 ? line : line.PreviousLine;
                    var newLine = delimited != null && delimited.DelimiterLength > 0
                        ? document.GetText(delimited.EndOffset, delimited.DelimiterLength)
                        : Environment.NewLine;

                    document.Insert(line.EndOffset, newLine + document.GetText(line.Offset, line.Length));
                }
            }
            finally
            {
                document.EndUpdate();
            }

            if (caretLine > 0)
            {
                caret.Line = caretLine;
                caret.Column = caretColumn;
            }

            caret.BringCaretToView();
        }
    }
}
