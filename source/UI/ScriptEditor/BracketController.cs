using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace RevitDevKit.UI.ScriptEditor
{
    // Brackets and quotes as in Visual Studio: ( [ { " ' insert their closing character, typing that closing character
    // steps over it, Backspace between an empty pair removes both, and Enter between {} opens a block with { on its own line.
    // Only characters inserted here are stepped over, so a closing one typed anywhere else is inserted as usual.
    internal sealed class BracketController
    {
        // Closed as soon as the opening character is typed: a bracket always opens a pair.
        private static readonly Dictionary<char, char> Pairs = new Dictionary<char, char> { { '(', ')' }, { '[', ']' }, { '{', '}' } };

        // Closed when typed too, but only where they open a literal (see OpensLiteral): the same character also
        // ends a string, and an apostrophe may just be text in a comment.
        private const string Quotes = "\"'";

        // Everything this class may have closed, whoever decided it: Pairs, Quotes, and '<' that CompletionController
        // closes through Close once it knows the '<' opens a generic and is not a comparison. Their closing characters
        // are the ones stepped over when typed, and Backspace removes such a pair while it is still empty.
        private static readonly Dictionary<char, char> AllPairs = new Dictionary<char, char>
        {
            { '(', ')' }, { '[', ']' }, { '{', '}' }, { '<', '>' }, { '"', '"' }, { '\'', '\'' }
        };

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

        // Alt + drag selects a box, and what is typed goes into every line of it. A pair closed, stepped over or
        // removed at the caret would change one line only, so nothing of the kind happens while a box is selected;
        // CompletionController opens no lists then either.
        internal static bool IsBoxSelection(TextArea area) => area.Selection is RectangleSelection;

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
            if (e.Text.Length != 1 || IsBoxSelection(_editor.TextArea))
                return;

            var typed = e.Text[0];
            var isQuote = Quotes.IndexOf(typed) >= 0;
            if (!isQuote && !Pairs.ContainsKey(typed))
                return;

            // Only before whitespace, a line end or another closing character, so ( typed in front of a word stays alone.
            if (Caret < Document.TextLength && !ClosesHere(Document.GetCharAt(Caret)))
                return;

            if (isQuote && !OpensLiteral())
                return;

            Close(isQuote ? typed : Pairs[typed]);
        }

        private void OnTextEntering(TextCompositionEventArgs e)
        {
            if (e.Text.Length != 1 || !AllPairs.ContainsValue(e.Text[0]) || IsBoxSelection(_editor.TextArea))
                return;

            var caret = Caret;
            if (Quotes.IndexOf(e.Text[0]) >= 0 && IsEscaped(caret, e.Text[0]))
                return;

            var inserted = InsertedAt(caret, e.Text[0]);
            if (inserted == null)
                return;

            _inserted.Remove(inserted);
            Caret = caret + 1;
            e.Handled = true;
        }

        private void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || IsBoxSelection(_editor.TextArea))
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

        // Whether the quote just typed starts a string or char literal. Not after a letter or digit (don't),
        // and not inside a literal or a // comment that the line before it has open.
        private bool OpensLiteral()
        {
            var before = LineBefore(Caret - 1);
            if (before.Length > 0 && (char.IsLetterOrDigit(before[before.Length - 1]) || before[before.Length - 1] == '_'))
                return false;

            return OpenAt(before, out _) == '\0';
        }

        // \" in "..." is a quote inside the string, not its end. "\\" ends with an escaped backslash, and @"..." has no escapes.
        private bool IsEscaped(int caret, char quote)
        {
            var before = LineBefore(caret);
            if (OpenAt(before, out var verbatim) != quote || verbatim)
                return false;

            var backslashes = before.Length - before.TrimEnd('\\').Length;
            return backslashes % 2 == 1;
        }

        private string LineBefore(int offset)
        {
            var line = Document.GetLineByOffset(offset);
            return Document.GetText(line.Offset, offset - line.Offset);
        }

        // What the line leaves open at its end: '"' or '\'' inside a literal (verbatim for @"..."), '/' in a // comment,
        // '\0' in code.
        private static char OpenAt(string text, out bool verbatim)
        {
            var open = '\0';
            verbatim = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (open == '\0')
                {
                    if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                        return '/';
                    if (c == '"' || c == '\'')
                    {
                        open = c;
                        verbatim = c == '"' && i > 0 && (text[i - 1] == '@' || i > 1 && text[i - 1] == '$' && text[i - 2] == '@');
                    }
                }
                else if (!verbatim && c == '\\')
                {
                    i++; // the escaped character
                }
                else if (c == open)
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i++; // "" inside @"..."
                    }
                    else
                    {
                        open = '\0';
                        verbatim = false;
                    }
                }
            }

            return open;
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
