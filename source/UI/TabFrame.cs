using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitDevKit.UI
{
    // Draws the selected tab and the editor frame as a single outline, so the rounded
    // corners and the concave joints match exactly.
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
            if (ActualWidth < 1 || ActualHeight < 1 || Stroke == null) return;

            var pen = new Pen(Stroke, 1);
            pen.Freeze();

            var r = Radius;

            double left = 0.5, right = ActualWidth - 0.5, bottom = ActualHeight - 0.5;

            if (_tab.IsEmpty || _tab.Height < 2 * r)
            {
                dc.DrawRoundedRectangle(null, pen, new Rect(left, left, right - left, bottom - left), r, r);
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

        private static void ArcTo(StreamGeometryContext c, double x, double y, double radius, SweepDirection direction)
        {
            c.ArcTo(new Point(x, y), new Size(radius, radius), 0, false, direction, true, false);
        }
    }
}
