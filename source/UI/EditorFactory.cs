using System;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using RevitDevKit.Core;

namespace RevitDevKit.UI
{
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
                SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#")
            };

            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;

            editor.TextArea.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control) return;

                Duplicate(editor);
                e.Handled = true;
            };

            return editor;
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
                                    " из " + (string.IsNullOrEmpty(location) ? "<память>" : location));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось определить версию AvalonEdit: " + ex.Message);
            }
        }
    }
}
