using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FilmKonverter.Core.Jobs;

namespace FilmKonverter.App;

internal static class Palette
{
    public static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Green = B("#2EA043");
    public static readonly SolidColorBrush Blue = B("#2F81F7");
    public static readonly SolidColorBrush Slate = B("#5F6B7A");
    public static readonly SolidColorBrush Amber = B("#D29922");
    public static readonly SolidColorBrush Gray = B("#6E7681");
    public static readonly SolidColorBrush Red = B("#DA3633");
    public static readonly SolidColorBrush TextOk = B("#3FB950");
    public static readonly SolidColorBrush TextWarn = B("#E3B341");
    public static readonly SolidColorBrush TextErr = B("#F85149");

    public static Brush Resource(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

public sealed class LineKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LineKind.Ok => Palette.TextOk,
        LineKind.Warn => Palette.TextWarn,
        LineKind.Error => Palette.TextErr,
        LineKind.Dim => Palette.Resource("TextFillColorTertiaryBrush", Palette.Gray),
        _ => Palette.Resource("TextFillColorPrimaryBrush", Brushes.White),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class OutcomeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Outcome.Encoded => Palette.Green,
        Outcome.Replaced => Palette.Blue,
        Outcome.AlreadyOk => Palette.Slate,
        Outcome.Skipped => Palette.Amber,
        Outcome.OtherPc => Palette.Gray,
        Outcome.Cancelled => Palette.Gray,
        _ => Palette.Red,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "|").Split('|');
        return value is true ? parts[0] : parts.ElementAtOrDefault(1) ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class WarnBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var warn = value is true;
        if (parameter as string == "peer") return warn ? Palette.TextOk : Palette.Gray;
        if (parameter as string == "invert") warn = !warn;
        return warn ? Palette.TextWarn : Palette.TextOk;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RenameStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Core.Rename.RenameStatus.Ready => Palette.Green,
        Core.Rename.RenameStatus.Check => Palette.Amber,
        Core.Rename.RenameStatus.Conflict => Palette.Red,
        Core.Rename.RenameStatus.Unknown => Palette.Gray,
        _ => Palette.Slate,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = values.Length > 0 && values[0] is double p ? p : 0;
        var width = values.Length > 1 && values[1] is double w ? w : 0;
        return Math.Max(0, Math.Min(width, width * pct / 100d));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}
