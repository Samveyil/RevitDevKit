using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace RevitDevKit.UI.ScriptEditor
{
    internal enum CommentAction
    {
        Comment,
        Uncomment,
        Toggle
    }

    // Comments as in Visual Studio: Ctrl+K, Ctrl+C comments, Ctrl+K, Ctrl+U uncomments, Ctrl+/ toggles.
    // Whole lines get "// " at the indentation of the least indented of them; a part of one line is wrapped
    // in /* */. Each command is one undo step, and the selection stays on the same text.
    internal sealed class CommentController
    {
        private const string LineComment = "//";
        private const string BlockStart = "/*";
        private const string BlockEnd = "*/";

        private readonly TextEditor _editor;
        private bool _chord;

        private CommentController(TextEditor editor)
        {
            _editor = editor;
            editor.TextArea.PreviewKeyDown += (s, e) => EditorGuard.Run(() => OnPreviewKeyDown(e));
            editor.TextArea.LostKeyboardFocus += (s, e) => _chord = false;
        }

        public static void Attach(TextEditor editor)
        {
            new CommentController(editor);
        }

        private void OnPreviewKeyDown(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var control = Keyboard.Modifiers == ModifierKeys.Control;

            if (_chord)
            {
                if (IsModifier(key))
                    return;

                // The second key of the chord: anything else ends it and does what it does by itself.
                _chord = false;
                if (control && key == Key.C)
                    Run(CommentAction.Comment, e);
                else if (control && key == Key.U)
                    Run(CommentAction.Uncomment, e);
                return;
            }

            if (!control)
                return;

            if (key == Key.K)
            {
                _chord = true;
                e.Handled = true;
            }
            // The '/' key, whatever the layout types on it, and '/' on the numpad.
            else if (key == Key.Oem2 || key == Key.Divide)
            {
                Run(CommentAction.Toggle, e);
            }
        }

        private void Run(CommentAction action, KeyEventArgs e)
        {
            Apply(_editor, action);
            e.Handled = true;
        }

        private static bool IsModifier(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt;

        public static void Apply(TextEditor editor, CommentAction action)
        {
            var document = editor.Document;
            var selection = editor.TextArea.Selection;

            if (!selection.IsEmpty && !(selection is RectangleSelection))
            {
                var start = editor.SelectionStart;
                var end = start + editor.SelectionLength;
                var line = document.GetLineByOffset(start);

                if (line == document.GetLineByOffset(end) && IsPartOfLine(document, line, start, end) &&
                    Block(editor, start, end, action))
                    return;
            }

            Lines(editor, action);
        }

        // A selection inside one line that leaves some of the line's text out.
        private static bool IsPartOfLine(TextDocument document, DocumentLine line, int start, int end) =>
            document.GetText(start, end - start).Trim() != document.GetText(line.Offset, line.Length).Trim();

        // /* */ around the selection, without the spaces at its ends, or off it. False when the lines way fits better:
        // uncommenting a selection that is no block, or commenting one that has a "*/" of its own.
        private static bool Block(TextEditor editor, int start, int end, CommentAction action)
        {
            var document = editor.Document;
            var text = document.GetText(start, end - start);
            var inner = text.Trim();
            var from = start + (text.Length - text.TrimStart().Length);
            var to = end - (text.Length - text.TrimEnd().Length);
            var isBlock = inner.Length >= BlockStart.Length + BlockEnd.Length &&
                          inner.StartsWith(BlockStart, StringComparison.Ordinal) &&
                          inner.EndsWith(BlockEnd, StringComparison.Ordinal);

            var uncomment = action == CommentAction.Uncomment || action == CommentAction.Toggle && isBlock;
            if (uncomment ? !isBlock : inner.Contains(BlockEnd))
                return false;

            document.BeginUpdate();
            try
            {
                if (uncomment)
                {
                    document.Remove(to - BlockEnd.Length, BlockEnd.Length);
                    document.Remove(from, BlockStart.Length);
                    to -= BlockStart.Length + BlockEnd.Length;
                }
                else
                {
                    document.Insert(to, BlockEnd);
                    document.Insert(from, BlockStart);
                    to += BlockStart.Length + BlockEnd.Length;
                }
            }
            finally
            {
                document.EndUpdate();
            }

            editor.Select(from, to - from);
            return true;
        }

        private static void Lines(TextEditor editor, CommentAction action)
        {
            var document = editor.Document;
            var area = editor.TextArea;
            var selection = area.Selection;
            var box = selection is RectangleSelection;

            // A box is checked first: one of zero width (carets on several lines) is empty too.
            int first, last;
            if (box)
            {
                first = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line);
                last = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line);
            }
            else if (selection.IsEmpty)
            {
                first = last = area.Caret.Line;
            }
            else
            {
                var start = editor.SelectionStart;
                var end = start + editor.SelectionLength;
                first = document.GetLineByOffset(start).LineNumber;
                var endLine = document.GetLineByOffset(end);
                // A selection of whole lines ends at the start of the next one, which it does not take.
                last = end == endLine.Offset && endLine.LineNumber > first ? endLine.LineNumber - 1 : endLine.LineNumber;
            }

            var lines = Enumerable.Range(first, last - first + 1)
                .Select(document.GetLineByNumber)
                .Where(l => Indent(document, l) < l.Length)
                .ToList();
            if (lines.Count == 0)
                return;

            var uncomment = action == CommentAction.Uncomment ||
                            action == CommentAction.Toggle && lines.All(l => IsCommented(document, l));

            // Where the selection and the caret are, as line and column, moved with the text of their line below.
            var startPosition = Position(document, editor.SelectionStart);
            var endPosition = Position(document, editor.SelectionStart + editor.SelectionLength);
            var caretPosition = Position(document, area.Caret.Offset);
            var edits = new Dictionary<int, Edit>();

            document.BeginUpdate();
            try
            {
                if (uncomment)
                {
                    foreach (var line in lines.Where(l => IsCommented(document, l)))
                    {
                        var at = Indent(document, line);
                        var length = LineComment.Length;
                        if (at + length < line.Length && document.GetCharAt(line.Offset + at + length) == ' ')
                            length++;

                        document.Remove(line.Offset + at, length);
                        edits[line.LineNumber] = new Edit(at, -length);
                    }
                }
                else
                {
                    var at = lines.Min(l => Indent(document, l));
                    var comment = LineComment + " ";
                    foreach (var line in lines)
                    {
                        document.Insert(line.Offset + at, comment);
                        edits[line.LineNumber] = new Edit(at, comment.Length);
                    }
                }
            }
            finally
            {
                document.EndUpdate();
            }

            if (box)
            {
                // The box would cut through the comments; the lines it covered stay selected instead.
                var from = document.GetLineByNumber(first).Offset;
                editor.Select(from, document.GetLineByNumber(last).EndOffset - from);
            }
            else if (!selection.IsEmpty)
            {
                var from = Offset(document, startPosition, edits, false);
                editor.Select(from, Offset(document, endPosition, edits, true) - from);
            }
            else
            {
                area.Caret.Offset = Offset(document, caretPosition, edits, true);
            }

            area.Caret.BringCaretToView();
        }

        private static int Indent(TextDocument document, DocumentLine line)
        {
            var i = 0;
            while (i < line.Length && char.IsWhiteSpace(document.GetCharAt(line.Offset + i)))
                i++;
            return i;
        }

        private static bool IsCommented(TextDocument document, DocumentLine line)
        {
            var at = Indent(document, line);
            return at + LineComment.Length <= line.Length &&
                   document.GetText(line.Offset + at, LineComment.Length) == LineComment;
        }

        private static Tuple<int, int> Position(TextDocument document, int offset)
        {
            var line = document.GetLineByOffset(offset);
            return Tuple.Create(line.LineNumber, offset - line.Offset);
        }

        // The new offset of a point whose line was edited. The start of a selection right at the inserted comment
        // takes it in, the end and the caret stay after it.
        private static int Offset(TextDocument document, Tuple<int, int> position, Dictionary<int, Edit> edits, bool after)
        {
            var line = document.GetLineByNumber(position.Item1);
            var column = position.Item2;

            if (edits.TryGetValue(position.Item1, out var edit))
            {
                if (edit.Length > 0)
                {
                    if (column > edit.At || after && column == edit.At)
                        column += edit.Length;
                }
                else if (column > edit.At)
                {
                    column = Math.Max(edit.At, column + edit.Length);
                }
            }

            return line.Offset + Math.Min(column, line.Length);
        }

        private struct Edit
        {
            public Edit(int at, int length)
            {
                At = at;
                Length = length;
            }

            public int At { get; }
            public int Length { get; }
        }
    }
}
