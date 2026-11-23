using System.Globalization;

namespace FilmKonverter.Core.Util;

public sealed record TimeWindow(TimeSpan From, TimeSpan To)
{
    public static TimeWindow? Parse(string from, string to) =>
        TryParseTime(from, out var f) && TryParseTime(to, out var t) ? new TimeWindow(f, t) : null;

    public static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = default;
        var s = (text ?? "").Trim().Replace('.', ':');
        if (s.Length is 1 or 2 && int.TryParse(s, out var h) && h is >= 0 and <= 24) { value = TimeSpan.FromHours(h % 24); return true; }
        if (!TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out value)) return false;
        return value < TimeSpan.FromDays(1);
    }

    public bool IsOpen(DateTime now)
    {
        if (From == To) return true;
        var t = now.TimeOfDay;
        return From < To ? t >= From && t < To : t >= From || t < To;
    }

    public DateTime NextOpen(DateTime now)
    {
        if (IsOpen(now)) return now;
        var d = now.Date + From;
        return d > now ? d : d.AddDays(1);
    }

    public override string ToString() => $"{From:hh\\:mm}–{To:hh\\:mm}";
}
