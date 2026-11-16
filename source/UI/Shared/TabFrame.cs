using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitDevKit.UI.Shared
{
    // Draws the selected tab and the editor frame as a single outline, so the rounded
    // corners and the concave joints match exactly. With Fill it also paints the selected tab along that outline.
    public sealed class TabFrame : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(TabFrame),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(TabFrame),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        // The selected tab's background. A frame with a Fill and no Stroke goes under the tabs, one with a Stroke over them.
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(TabFrame),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public double Radius
        {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }

        private Rect _tab = Rect.Empty;

        public TabFrame()
        {
            IsHitTestVisible = false;

            LayoutUpdated += (s, e) =>
            {
                var tab = FindSelectedTab();
                if (tab == _tab) return;

                _tab = tab;
                InvalidateVisual();
            };
        }

        private Rect FindSelectedTab()
        {
            if (!(TemplatedParent is TabControl tabs) || tabs.SelectedItem == null) return Rect.Empty;

            if (!(tabs.ItemContainerGenerator.ContainerFromItem(tabs.SelectedItem) is TabItem item) || !item.IsVisible)
                return Rect.Empty;

            try
            {
                return item.TransformToVisual(this).TransformBounds(new Rect(item.RenderSize));
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 1 || ActualHeight < 1 || (Stroke == null && Fill == null)) return;

            var r = Radius;

            double left = 0.5, right = ActualWidth - 0.5, bottom = ActualHeight - 0.5;

            if (_tab.IsEmpty || _tab.Height < 2 * r)
            {
                if (Stroke != null)
                    dc.DrawRoundedRectangle(null, NewPen(), new Rect(left, left, right - left, bottom - left), r, r);
                return;
            }

            var top = Math.Round(_tab.Bottom) + 0.5;
            var tabTop = Math.Round(_tab.Top) + 0.5;
            var tabLeft = Math.Max(left, Math.Round(_tab.Left) + 0.5);
            var tabRight = Math.Min(right, Math.Round(_tab.Right) - 0.5);

            var leftFlare = tabLeft - left >= 2 * r;
            var rightFlare = right - tabRight >= 2 * r;
            if (!leftFlare) tabLeft = left;
            if (!rightFlare) tabRight = right;

            // The selected tab's background: the same corners as its outline, down through the frame's line, so no light
            // pixels are left between the fill and the stroke.
            if (Fill != null)
            {
                var tabShape = new StreamGeometry();
                using (var c = tabShape.Open())
                {
                    c.BeginFigure(new Point(tabLeft, top + 0.5), true, true);
                    c.LineTo(new Point(tabLeft, tabTop + r), false, false);
                    ArcTo(c, tabLeft + r, tabTop, r, SweepDirection.Clockwise);
                    c.LineTo(new Point(tabRight - r, tabTop), false, false);
                    ArcTo(c, tabRight, tabTop + r, r, SweepDirection.Clockwise);
                    c.LineTo(new Point(tabRight, top + 0.5), false, false);
                }
                tabShape.Freeze();
                dc.DrawGeometry(Fill, null, tabShape);
            }

            if (Stroke == null) return;

            var pen = NewPen();

            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(new Point(left, bottom - r), false, true);

                if (leftFlare)
                {
                    c.LineTo(new Point(left, top + r), true, false);
                    ArcTo(c, left + r, top, r, SweepDirection.Clockwise);
                    c.LineTo(new Point(tabLeft - r, top), true, false);
                    ArcTo(c, tabLeft, top - r, r, SweepDirection.Counterclockwise);
                }

                c.LineTo(new Point(tabLeft, tabTop + r), true, false);
                ArcTo(c, tabLeft + r, tabTop, r, SweepDirection.Clockwise);
                c.LineTo(new Point(tabRight - r, tabTop), true, false);
                ArcTo(c, tabRight, tabTop + r, r, SweepDirection.Clockwise);

                if (rightFlare)
                {
                    c.LineTo(new Point(tabRight, top - r), true, false);
                    ArcTo(c, tabRight + r, top, r, SweepDirection.Counterclockwise);
                    c.LineTo(new Point(right - r, top), true, false);
                    ArcTo(c, right, top + r, r, SweepDirection.Clockwise);
                }

                c.LineTo(new Point(right, bottom - r), true, false);
                ArcTo(c, right - r, bottom, r, SweepDirection.Clockwise);
                c.LineTo(new Point(left + r, bottom), true, false);
                ArcTo(c, left, bottom - r, r, SweepDirection.Clockwise);
            }
            geometry.Freeze();

            dc.DrawGeometry(null, pen, geometry);
        }

        private Pen NewPen()
        {
            var pen = new Pen(Stroke, 1);
            pen.Freeze();
            return pen;
        }

        private static void ArcTo(StreamGeometryContext c, double x, double y, double radius, SweepDirection direction)
        {
            c.ArcTo(new Point(x, y), new Size(radius, radius), 0, false, direction, true, false);
        }
    }
}
