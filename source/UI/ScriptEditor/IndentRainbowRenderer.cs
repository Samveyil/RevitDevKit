using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Windows;
using System.Windows.Media;

namespace RevitDevKit.UI.ScriptEditor
{
    // Indent Rainbow as in the Visual Studio extension: every indentation level of the visible lines gets its own
    // faint colour, and indentation that is not a whole number of levels is red. On top of the colours, a thin line
    // runs down the left edge of each level, where the { of that block stands, as Visual Studio's indent guides do.
    // A blank line takes the indentation of the next line with code, so bands and lines do not break inside a block.
    // Only the lines on screen are drawn, on every redraw of the editor; it costs a few rectangles per line.
    internal sealed class IndentRainbowRenderer : IBackgroundRenderer
    {
        // The extension's colours, stronger than its 7% as the editor background here is white.
        private static readonly Brush[] Levels =
        {
            Freeze(Color.FromArgb(0x30, 0xFF, 0xFF, 0x40)),
            Freeze(Color.FromArgb(0x30, 0x7F, 0xFF, 0x7F)),
            Freeze(Color.FromArgb(0x30, 0xFF, 0x7F, 0xFF)),
            Freeze(Color.FromArgb(0x30, 0x4F, 0xEC, 0xEC))
        };

        private static readonly Brush Misaligned = Freeze(Color.FromArgb(0x40, 0xC0, 0x20, 0x20));

        private static readonly Pen Guide = FreezePen(Color.FromArgb(0x40, 0x00, 0x00, 0x00));

        // How far down a blank line looks for the next line with code.
        private const int BlankLookAhead = 50;

        private readonly TextEditor _editor;

        public IndentRainbowRenderer(TextEditor editor)
        {
            _editor = editor;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid)
                return;

            var document = textView.Document;
            var levelSize = _editor.Options.IndentationSize;
            var columnWidth = textView.WideSpaceWidth;
            var left = -textView.HorizontalOffset;

            foreach (var visualLine in textView.VisualLines)
            {
                var line = visualLine.FirstDocumentLine;
                var indent = Indent(document, line, out var blank);
                if (blank)
                    indent = NextCodeIndent(document, line);
                if (indent == 0)
                    continue;

                var top = visualLine.VisualTop - textView.VerticalOffset;

                if (!blank && indent % levelSize != 0)
                {
                    drawingContext.DrawRectangle(Misaligned, null, new Rect(left, top, indent * columnWidth, visualLine.Height));
                }
                else
                {
                    for (var level = 0; (level + 1) * levelSize <= indent; level++)
                    {
                        var x = left + level * levelSize * columnWidth;
                        drawingContext.DrawRectangle(Levels[level % Levels.Length], null, new Rect(x, top, levelSize * columnWidth, visualLine.Height));
                    }
                }

                // A line at every level the text starts to the right of; snapped to a pixel so it stays one pixel thin.
                for (var column = 0; column < indent; column += levelSize)
                {
                    var x = Math.Floor(left + column * columnWidth) + 0.5;
                    drawingContext.DrawLine(Guide, new Point(x, top), new Point(x, top + visualLine.Height));
                }
            }
        }

        // Leading whitespace in columns, a tab counting up to the next indentation stop.
        private int Indent(TextDocument document, DocumentLine line, out bool blank)
        {
            var columns = 0;
            var levelSize = _editor.Options.IndentationSize;

            for (var offset = line.Offset; offset < line.EndOffset; offset++)
            {
                var c = document.GetCharAt(offset);
                if (c == ' ')
                    columns++;
                else if (c == '\t')
                    columns += levelSize - columns % levelSize;
                else
                {
                    blank = false;
                    return columns;
                }
            }

            blank = true;
            return columns;
        }

        private int NextCodeIndent(TextDocument document, DocumentLine line)
        {
            var next = line.NextLine;
            for (var i = 0; next != null && i < BlankLookAhead; i++, next = next.NextLine)
            {
                var indent = Indent(document, next, out var blank);
                if (!blank)
                    return indent;
            }

            return 0;
        }

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FreezePen(Color color)
        {
            var pen = new Pen(Freeze(color), 1);
            pen.Freeze();
            return pen;
        }
    }
}
