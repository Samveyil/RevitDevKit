using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RevitDevKit.UI.Shared
{
    // Visible for true, collapsed for false; the other way round with Invert. In Theme.xaml as BoolToVisibility
    // and InverseBoolToVisibility, for an element shown by a flag instead of a style with a DataTrigger.
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is bool flag && flag) != Invert ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is Visibility visibility && visibility == Visibility.Visible) != Invert;
    }

    // Visible when the value equals the parameter, compared as text: the toolbar of tab 2 (SelectedTabIndex, 2),
    // the hint of an empty list (Items.Count, 0). In Theme.xaml as EqualsToVisibility.
    public sealed class EqualsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture),
                System.Convert.ToString(parameter, CultureInfo.InvariantCulture), StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
