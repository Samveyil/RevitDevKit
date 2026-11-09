using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitDevKit.UI.ScriptEditor
{
    // Multi-line editing as in Visual Studio on top of AvalonEdit's box selection (Alt + drag, Shift+Alt+arrows).
    // AvalonEdit draws the caret on one line of the box only, so a caret is drawn on every other line too.
    // Tab adds spaces up to the next indentation stop at the caret of every line, Shift+Tab takes one level off the
    // start of every line, and the box stays, so the group keeps typing together.
    internal sealed class BoxSelectionController : IBackgroundRenderer
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly TextEditor _editor;
        private UIElement _caretLayer;
        private FieldInfo _caretShown;
        private FieldInfo _caretBlinkOn;
        private bool _caretLayerSought;

        private BoxSelectionController(TextEditor editor)
        {
            _editor = editor;
            var area = editor.TextArea;
            area.TextView.BackgroundRenderers.Add(this);
            area.PreviewKeyDown += (s, e) => EditorGuard.Run(() => OnPreviewKeyDown(e));
            area.SelectionChanged += (s, e) => EditorGuard.Run(() =>
            {
                UpdateSelectionOutline();
                Invalidate();
            });
            area.TextView.ScrollOffsetChanged += (s, e) => EditorGuard.Run(InvalidateBox);
            area.TextView.VisualLinesChanged += (s, e) => EditorGuard.Run(InvalidateBox);
        }

        public static void Attach(TextEditor editor)
        {
            new BoxSelectionController(editor);
        }

        // Drawn by AvalonEdit's caret layer together with its own caret.
        public KnownLayer Layer => KnownLayer.Caret;

        private TextArea Area => _editor.TextArea;

        private RectangleSelection Box => Area.Selection as RectangleSelection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            // An exception while rendering would take Revit down.
            EditorGuard.Run(() => DrawCarets(textView, drawingContext));
        }

        private void DrawCarets(TextView textView, DrawingContext drawingContext)
        {
            var box = Box;
            if (box == null || !Area.IsKeyboardFocusWithin || !textView.VisualLinesValid || !MainCaretBlinkOn(textView))
                return;

            var first = Math.Min(box.StartPosition.Line, box.EndPosition.Line);
            var last = Math.Max(box.StartPosition.Line, box.EndPosition.Line);
            var caretLine = Area.Caret.Line;
            var column = Area.Caret.VisualColumn;
            // The brush and the size AvalonEdit's own caret has.
            var brush = textView.GetValue(TextBlock.ForegroundProperty) as Brush ?? Brushes.Black;
            var pixel = PixelSize(textView);

            foreach (var visualLine in textView.VisualLines)
            {
                var number = visualLine.FirstDocumentLine.LineNumber;
                if (number < first || number > last || number == caretLine)
                    continue;

                var top = visualLine.GetVisualPosition(column, VisualYPosition.LineTop) - textView.ScrollOffset;
                var bottom = visualLine.GetVisualPosition(column, VisualYPosition.LineBottom) - textView.ScrollOffset;
                drawingContext.DrawRectangle(brush, null, new Rect(
                    Snap(top.X, pixel.Width), Snap(top.Y, pixel.Height),
                    Snap(SystemParameters.CaretWidth, pixel.Width), Snap(bottom.Y - top.Y, pixel.Height)));
            }
        }

        // The carets blink together with AvalonEdit's caret: they are drawn in its layer, which renders again on every
        // blink, and they read whether its caret is shown right now. The layer's state is private, so with another
        // AvalonEdit version where it is not found the carets are simply drawn without blinking.
        private bool MainCaretBlinkOn(TextView textView)
        {
            FindCaretLayer(textView);
            if (_caretShown == null || _caretBlinkOn == null)
                return true;

            // AvalonEdit hides its caret while its line is scrolled out of view; the other lines keep theirs.
            return !(bool)_caretShown.GetValue(_caretLayer) || (bool)_caretBlinkOn.GetValue(_caretLayer);
        }

        private void FindCaretLayer(TextView textView)
        {
            if (_caretLayerSought)
                return;

            _caretLayerSought = true;
            _caretLayer = textView.Layers.OfType<UIElement>().FirstOrDefault(l => l.GetType().Name == "CaretLayer");
            if (_caretLayer == null)
                return;

            var shown = _caretLayer.GetType().GetField("isVisible", Private);
            var blinkOn = _caretLayer.GetType().GetField("blink", Private);
            if (shown?.FieldType == typeof(bool) && blinkOn?.FieldType == typeof(bool))
            {
                _caretShown = shown;
                _caretBlinkOn = blinkOn;
            }
        }

        // A box of zero width is only carets, but AvalonEdit still outlines it with a line that merges with them,
        // so the outline is taken off while the box has no width, as in Visual Studio. A wider box keeps it.
        private void UpdateSelectionOutline()
        {
            var box = Box;
            if (box != null && Column(box.StartPosition) == Column(box.EndPosition))
            {
                if (Area.SelectionBorder != null)
                    Area.SelectionBorder = null;
            }
            else
            {
                // Back to the outline AvalonEdit's style gives.
                Area.ClearValue(TextArea.SelectionBorderProperty);
            }
        }

        // Scrolling moves the lines but AvalonEdit renders its caret layer again only when its own caret moves.
        private void InvalidateBox()
        {
            if (Box != null)
                Invalidate();
        }

        // Renders the caret layer alone. TextView.InvalidateLayer measures the whole view again, which restarts the
        // blinking of AvalonEdit's caret, so it is the fallback only.
        private void Invalidate()
        {
            FindCaretLayer(Area.TextView);
            if (_caretLayer != null)
                _caretLayer.InvalidateVisual();
            else
                Area.TextView.InvalidateLayer(KnownLayer.Caret);
        }

        private static Size PixelSize(Visual visual)
        {
            var target = PresentationSource.FromVisual(visual)?.CompositionTarget;
            if (target == null)
                return new Size(1, 1);

            var matrix = target.TransformToDevice;
            return new Size(1 / matrix.M11, 1 / matrix.M22);
        }

        // On whole device pixels, as AvalonEdit snaps its caret, so every caret is equally sharp.
        private static double Snap(double value, double pixel) =>
            Math.Round(value / pixel, MidpointRounding.AwayFromZero) * pixel;

        private void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key != Key.Tab || Box == null)
                return;

            if (Keyboard.Modifiers == ModifierKeys.None)
                Indent();
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
                Outdent();
            else
                return;

            e.Handled = true;
        }

        // Spaces up to the next indentation stop, typed into every line of the box: the box moves on with them.
        private void Indent()
        {
            var box = Box;
            var size = _editor.Options.IndentationSize;
            var column = Math.Min(Column(box.StartPosition), Column(box.EndPosition));
            box.ReplaceSelectionWithText(new string(' ', size - column % size));
        }

        // One indentation level off the start of every line of the box, as Shift+Tab does on a block of lines.
        // The box moves left with the text of the caret's line.
        private void Outdent()
        {
            var box = Box;
            var document = _editor.Document;
            var size = _editor.Options.IndentationSize;
            var start = box.StartPosition;
            var end = box.EndPosition;
            var caretLine = Area.Caret.Line;
            var shift = 0;

            document.BeginUpdate();
            try
            {
                for (var number = Math.Min(start.Line, end.Line); number <= Math.Max(start.Line, end.Line); number++)
                {
                    var line = document.GetLineByNumber(number);
                    var spaces = 0;
                    while (spaces < size && spaces < line.Length && document.GetCharAt(line.Offset + spaces) == ' ')
                        spaces++;

                    if (spaces > 0)
                        document.Remove(line.Offset, spaces);
                    if (number == caretLine)
                        shift = spaces;
                }
            }
            finally
            {
                document.EndUpdate();
            }

            var newStart = At(start.Line, Column(start) - shift);
            var newEnd = At(end.Line, Column(end) - shift);
            Area.Selection = new RectangleSelection(Area, newStart, newEnd);
            Area.Caret.Position = newEnd;
        }

        // A position created without a visual column has -1 there; with tabs turned into spaces it equals the text column.
        private static int Column(TextViewPosition position) =>
            position.VisualColumn >= 0 ? position.VisualColumn : position.Column - 1;

        // A position by visual column, past the line's end too: a box may stand in virtual space.
        private TextViewPosition At(int line, int visualColumn)
        {
            visualColumn = Math.Max(0, visualColumn);
            var length = _editor.Document.GetLineByNumber(line).Length;
            return new TextViewPosition(line, Math.Min(visualColumn, length) + 1, visualColumn);
        }
    }
}
