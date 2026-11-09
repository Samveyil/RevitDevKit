using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace RevitDevKit.UI.ScriptEditor
{
    // Brackets as in Visual Studio: ( [ { insert their closing bracket, typing that closing bracket steps over it,
    // Backspace between an empty pair removes both, and Enter between {} opens a block with { on its own line.
    // Only brackets inserted here are stepped over, so a closing bracket typed anywhere else is inserted as usual.
    // '<' is closed by CompletionController through Close, once it knows the '<' opens a generic and is not a comparison.
    internal sealed class BracketController
    {
        private static readonly Dictionary<char, char> Pairs = new Dictionary<char, char> { { '(', ')' }, { '[', ']' }, { '{', '}' } };
        private static readonly Dictionary<char, char> AllPairs = new Dictionary<char, char> { { '(', ')' }, { '[', ']' }, { '{', '}' }, { '<', '>' } };

        private readonly TextEditor _editor;
        private readonly List<TextAnchor> _inserted = new List<TextAnchor>();

        private BracketController(TextEditor editor)
        {
            _editor = editor;
            editor.TextArea.TextEntering += (s, e) => EditorGuard.Run(() => OnTextEntering(e));
            editor.TextArea.TextEntered += (s, e) => EditorGuard.Run(() => OnTextEntered(e));
            editor.TextArea.PreviewKeyDown += (s, e) => EditorGuard.Run(() => OnPreviewKeyDown(e));
            editor.Document.Changing += (s, e) => EditorGuard.Run(() => OnDocumentChanging(e));
        }

        // Attached by CompletionController, after its own handlers.
        public static BracketController Attach(TextEditor editor)
        {
            return new BracketController(editor);
        }

        // Inserts a closing bracket at the caret that is stepped over and removed with its pair like the ones inserted here.
        public void Close(char closing)
        {
            var caret = Caret;
            Document.Insert(caret, closing.ToString());
            _inserted.Add(Document.CreateAnchor(caret));
            Caret = caret;
        }

        private TextDocument Document => _editor.Document;

        private int Caret
        {
            get => _editor.TextArea.Caret.Offset;
            set => _editor.TextArea.Caret.Offset = value;
        }

        // A bracket deleted by hand is forgotten: otherwise its anchor would move on with the typed text
        // and a later ')' would be taken for it.
        private void OnDocumentChanging(DocumentChangeEventArgs e)
        {
            if (e.RemovalLength > 0)
                _inserted.RemoveAll(a => a.IsDeleted || a.Offset >= e.Offset && a.Offset < e.Offset + e.RemovalLength);
        }

        private void OnTextEntered(TextCompositionEventArgs e)
        {
            if (e.Text.Length != 1 || !Pairs.TryGetValue(e.Text[0], out var closing))
                return;

            // Only before whitespace, a line end or another closing character, so ( typed in front of a word stays alone.
            if (Caret < Document.TextLength && !ClosesHere(Document.GetCharAt(Caret)))
                return;

            Close(closing);
        }

        private void OnTextEntering(TextCompositionEventArgs e)
        {
            if (e.Text.Length != 1 || !AllPairs.ContainsValue(e.Text[0]))
                return;

            var caret = Caret;
            var inserted = InsertedAt(caret, e.Text[0]);
            if (inserted == null)
                return;

            _inserted.Remove(inserted);
            Caret = caret + 1;
            e.Handled = true;
        }

        private void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None)
                return;

            if (e.Key == Key.Back)
                e.Handled = RemoveEmptyPair();
            else if (e.Key == Key.Enter)
                e.Handled = OpenBlock();
        }

        private bool RemoveEmptyPair()
        {
            var caret = Caret;
            if (caret == 0 || caret >= Document.TextLength || !_editor.TextArea.Selection.IsEmpty)
                return false;

            if (!AllPairs.TryGetValue(Document.GetCharAt(caret - 1), out var closing) || InsertedAt(caret, closing) == null)
                return false;

            Document.Remove(caret - 1, 2);
            return true;
        }

        // "if (x) {|}" becomes
        //   if (x)
        //   {
        //       |
        //   }
        private bool OpenBlock()
        {
            var caret = Caret;
            if (caret == 0 || caret >= Document.TextLength || Document.GetCharAt(caret - 1) != '{' || Document.GetCharAt(caret) != '}')
                return false;

            var line = Document.GetLineByOffset(caret);
            var lineText = Document.GetText(line.Offset, line.Length);
            var indent = lineText.Substring(0, lineText.Length - lineText.TrimStart().Length);
            var head = Document.GetText(line.Offset, caret - 1 - line.Offset).TrimEnd();
            var newLine = NewLine(line);
            var unit = _editor.Options.IndentationString;

            var braceOnItsOwnLine = head.Trim().Length == 0;
            var start = braceOnItsOwnLine ? caret - 1 : line.Offset + head.Length;
            var opening = braceOnItsOwnLine ? "{" : newLine + indent + "{";

            Document.Replace(start, caret + 1 - start, opening + newLine + indent + unit + newLine + indent + "}");
            Caret = start + opening.Length + newLine.Length + indent.Length + unit.Length;
            return true;
        }

        private string NewLine(DocumentLine line)
        {
            var delimited = line.DelimiterLength > 0 ? line : line.PreviousLine;
            return delimited == null || delimited.DelimiterLength == 0
                ? Environment.NewLine
                : Document.GetText(delimited.EndOffset, delimited.DelimiterLength);
        }

        // The bracket inserted here at the offset, if it is still that bracket.
        private TextAnchor InsertedAt(int offset, char closing)
        {
            _inserted.RemoveAll(a => a.IsDeleted);
            if (offset >= Document.TextLength || Document.GetCharAt(offset) != closing)
                return null;

            return _inserted.FirstOrDefault(a => a.Offset == offset);
        }

        internal static bool ClosesHere(char next)
        {
            return char.IsWhiteSpace(next) || next == ')' || next == ']' || next == '}' || next == ';' || next == ',';
        }
    }
}
