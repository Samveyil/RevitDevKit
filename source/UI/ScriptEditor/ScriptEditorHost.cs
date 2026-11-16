using BIMPlugins.ExtStorage.Extensions;
using ICSharpCode.AvalonEdit;
using RevitDevKit.Core.Output;
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

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

    // Revit may already have an old AvalonEdit loaded (e.g. 4.3 from Dynamo),
    // so only members available in every version are used here.
    internal static class EditorFactory
    {
        private static bool _versionLogged;

        public static TextEditor Create()
        {
            LogAvalonEditVersionOnce();

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

            editor.TextArea.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control) return;

                Duplicate(editor);
                e.Handled = true;
            };

            return editor;
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
                ConsoleOutput.Debug("Угол полос прокрутки не перекрашен: " + ex.Message);
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

        private static void LogAvalonEditVersionOnce()
        {
            if (_versionLogged) return;
            _versionLogged = true;

            try
            {
                var assembly = typeof(TextEditor).Assembly;
                var location = assembly.Location;

                ConsoleOutput.Debug("AvalonEdit " + assembly.GetName().Version +
                                    " из " + (location.IsNullOrEmpty() ? "<память>" : location));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось определить версию AvalonEdit: " + ex.Message);
            }
        }
    }
}
