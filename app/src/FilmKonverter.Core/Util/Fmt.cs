using System.Globalization;

namespace FilmKonverter.Core.Util;

public static class Fmt
{
    public static CultureInfo De => L.Culture;
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Size(double bytes)
    {
        if (double.IsNaN(bytes) || bytes <= 0) return "-";
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("N2", De) + " GB"
            : (bytes / mb).ToString("N0", De) + " MB";
    }

    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 359999) return "--:--:--";
        var s = (long)Math.Floor(seconds);
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    public static string Mbit(double kbit) => (kbit / 1000d).ToString("N1", De);

    public static string Percent(double fraction, int decimals = 1) =>
        (fraction * 100).ToString("N" + decimals, De) + " %";

    public static double ParseDouble(string? s, double fallback = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var clean = new string(s.Where(c => char.IsDigit(c) || c is '.' or '-' or 'e' or 'E' or '+').ToArray());
        return double.TryParse(clean, NumberStyles.Float, Inv, out var v) ? v : fallback;
    }

    public static double ParseRate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split('/');
        var n = ParseDouble(parts[0], 0);
        var d = parts.Length > 1 ? ParseDouble(parts[1], 0) : 1;
        return d > 0 ? n / d : 0;
    }
}
