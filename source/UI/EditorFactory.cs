using System;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using RevitScriptRunner.Core;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Создаёт редактор кода на стандартных настройках AvalonEdit.
    ///
    /// Оформления здесь намеренно нет — ни цветов, ни шрифтовых настроек помимо
    /// моноширинного шрифта. Используются только те члены API, которые есть в
    /// AvalonEdit любых версий: в процессе Revit может оказаться чужая сборка
    /// ICSharpCode.AvalonEdit (у Dynamo, например, версия 4.3 от 2012 года), и
    /// обращение к более новому члену падает MissingMethodException.
    ///
    /// Тёмная схема лежит в CSharpDark.xshd — она понадобится, когда будем
    /// заниматься оформлением.
    /// </summary>
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

            // Поведение ввода, а не оформление: отступы пробелами по 4.
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;

            // Ctrl+D — дублировать строку или выделение, как в Visual Studio.
            editor.TextArea.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control) return;

                Duplicate(editor);
                e.Handled = true;
            };

            return editor;
        }

        /// <summary>
        /// Без выделения копирует текущую строку под неё и переводит каретку на копию
        /// в тот же столбец. С выделением вставляет его копию сразу за ним и выделяет её.
        /// Одно действие — один шаг отмены.
        /// </summary>
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

                    // Перевод строки берём тот, что уже в документе; у последней строки
                    // его нет — тогда у предыдущей.
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

        /// <summary>
        /// Пишет в консоль, какая именно сборка AvalonEdit используется.
        /// Полезно при разборе странностей редактора: версия зависит от того, какая
        /// надстройка Revit успела загрузить AvalonEdit первой.
        /// </summary>
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
