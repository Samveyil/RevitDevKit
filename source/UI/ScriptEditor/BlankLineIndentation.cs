using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;

namespace RevitDevKit.UI.ScriptEditor
{
    // Enter keeps the indentation of the line above, as AvalonEdit does, and a line left with nothing but that
    // indentation becomes empty, as in Visual Studio: Enter on an indented blank line leaves no spaces behind.
    internal sealed class BlankLineIndentation : DefaultIndentationStrategy
    {
        public override void IndentLine(TextDocument document, DocumentLine line)
        {
            // The new line takes the indentation first, while the line above still has it.
            base.IndentLine(document, line);

            var previous = line?.PreviousLine;
            if (previous == null || previous.Length == 0)
                return;

            var indentation = TextUtilities.GetWhitespaceAfter(document, previous.Offset);
            if (indentation.Length == previous.Length)
                document.Remove(indentation);
        }
    }
}
