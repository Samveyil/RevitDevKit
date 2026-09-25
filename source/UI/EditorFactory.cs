using System;
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

            return editor;
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
