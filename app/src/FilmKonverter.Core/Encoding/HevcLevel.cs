namespace FilmKonverter.Core.Encoding;

public static class HevcLevel
{
    public sealed record Level(string Name, int Idc);

    private static readonly (Level L, long Ps, long Sr)[] Table =
    [
        (new("4.1", 123), 2_228_224, 133_693_440),
        (new("5.0", 150), 8_912_896, 267_386_880),
        (new("5.1", 153), 8_912_896, 534_773_760),
        (new("5.2", 156), 8_912_896, 1_069_547_520),
        (new("6.1", 183), 35_651_584, 2_139_095_040),
        (new("6.2", 186), 35_651_584, 4_278_190_080),
    ];

    public static Level For(int width, int height, double fps)
    {
        long ps = (long)Math.Max(1, width) * Math.Max(1, height);
        var f = fps is > 0 and < 1000 ? fps : 30;
        foreach (var (l, maxPs, maxSr) in Table)
            if (ps <= maxPs && ps * f <= maxSr) return l;
        return Table[^1].L;
    }

    public static double FpsFor(Media.StreamInfo video)
    {
        var avg = Util.Fmt.ParseRate(video.AvgFrameRate);
        if (avg > 0) return avg;
        var r = Util.Fmt.ParseRate(video.RFrameRate);
        return r is > 0 and <= 240 ? r : 30;
    }

    public const int HighestTrusted = 153;

    public static Level? FixFor(Media.MediaInfo media)
    {
        if (media.Video is not { CodecName: "hevc" } v || v.Level <= HighestTrusted) return null;
        var target = For(v.Width, v.Height, FpsFor(v));
        return v.Level > target.Idc ? target : null;
    }

    public static string Text(int idc) => (idc / 30.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public static (int W, int H) Fit(int width, int height, int maxW, int maxH)
    {
        if (maxW <= 0 || maxH <= 0 || (width <= maxW && height <= maxH)) return (width, height);
        var s = Math.Min(maxW / (double)width, maxH / (double)height);
        return ((int)Math.Round(width * s / 2) * 2, (int)Math.Round(height * s / 2) * 2);
    }
}
