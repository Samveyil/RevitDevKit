using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace RevitDevKit.UI
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
}
