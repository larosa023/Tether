using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Tether.UI;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullableIntConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i ? i : 0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value;
}

/// <summary>Shows the "use as output" button: connected audio device that isn't already the default.</summary>
public sealed class CanUseAsOutputConverter : IMultiValueConverter
{
    public object Convert(object[] v, Type t, object p, CultureInfo c) =>
        v.Length == 3 && v[0] is true && v[1] is true && v[2] is false ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
