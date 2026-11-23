using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public enum PictureMatch
{
    Same,
    Different,
    Unknown,
}

public static class ContentCheck
{
    public const double TailSeconds = 20;
    public const double WholeSlack = 5;
    public const double MaxDifference = 10;
    public const int MinContrast = 48;

    private static readonly double[] Positions = [0.15, 0.3, 0.5, 0.7, 0.85];
    private const int Side = 32;

    public static async Task<MediaInfo?> ProbeAsync(FfmpegTools tools, string file, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(400, ct).ConfigureAwait(false);
            try
            {
                var m = await tools.ProbeAsync(file, ct).ConfigureAwait(false);
                if (m?.Video is not null) return m;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
        return null;
    }

    public static async Task<double?> TailEndAsync(FfmpegTools tools, string file, double durationSeconds, CancellationToken ct = default)
    {
        if (double.IsNaN(durationSeconds) || durationSeconds <= 0) return null;
        double? best = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(700 * attempt, ct).ConfigureAwait(false);
            var end = await TailEndOnceAsync(tools, file, durationSeconds, ct).ConfigureAwait(false);
            if (end is double e && (best is null || e > best)) best = e;
            if (IsWhole(durationSeconds, best)) break;
        }
        return best;
    }

    private static async Task<double?> TailEndOnceAsync(FfmpegTools tools, string file, double durationSeconds, CancellationToken ct)
    {
        var start = Math.Max(0, durationSeconds - TailSeconds);
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(tools.FfprobePath,
                ["-v", "error", "-read_intervals", start.ToString("0.###", Fmt.Inv) + "%", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file],
                TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (r.StdErr.Contains("Input/output error", StringComparison.OrdinalIgnoreCase) || r.StdErr.Contains("I/O error", StringComparison.OrdinalIgnoreCase)) return null;
        double? max = null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var field = line.Split(',')[0].Trim();
            if (double.TryParse(field, System.Globalization.NumberStyles.Float, Fmt.Inv, out var v) && !double.IsNaN(v) && (max is null || v > max)) max = v;
        }
        return max;
    }

    public static bool IsWhole(double durationSeconds, double? tailEnd) => tailEnd is double e && e >= durationSeconds - WholeSlack;

    public static async Task<byte[]?> ThumbAsync(FfmpegTools tools, string file, double seconds, int videoIndex = -1, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var b = await ThumbOnceAsync(tools, file, seconds, videoIndex, ct).ConfigureAwait(false);
            if (b is not null || attempt >= 2) return b;
            await Task.Delay(700 * (attempt + 1), ct).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> ThumbOnceAsync(FfmpegTools tools, string file, double seconds, int videoIndex, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gimbo-bild-" + Guid.NewGuid().ToString("N")[..10] + ".raw");
        try
        {
            List<string> args = ["-v", "error", "-nostdin", "-ss", Math.Max(0, seconds).ToString("0.###", Fmt.Inv), "-i", file];
            if (videoIndex >= 0) args.AddRange(["-map", "0:" + videoIndex.ToString(Fmt.Inv)]);
            args.AddRange(["-frames:v", "1", "-an", "-sn", "-dn", "-vf", $"scale={Side}:{Side}:flags=area,format=gray", "-f", "rawvideo", "-y", tmp]);
            await ProcessRunner.RunAsync(tools.FfmpegPath, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            var bytes = File.Exists(tmp) ? await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false) : [];
            return bytes.Length == Side * Side ? bytes : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static double Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    public static double Spread(byte[] a)
    {
        var mean = a.Sum(v => (long)v) / (double)a.Length;
        return a.Sum(v => Math.Abs(v - mean)) / a.Length;
    }

    public static (bool Same, bool Proof) Compare(byte[] x, byte[] y)
    {
        var difference = Difference(x, y);
        if (difference > MaxDifference) return (false, false);
        return (true, x.Max() - x.Min() >= MinContrast && difference <= Spread(x) / 4);
    }

    public static async Task<PictureMatch> SamePictureAsync(FfmpegTools tools, string a, string b, double lengthSeconds,
        int videoIndexA = -1, int videoIndexB = -1, CancellationToken ct = default)
    {
        if (double.IsNaN(lengthSeconds) || lengthSeconds <= 0) return PictureMatch.Unknown;
        var informative = 0;
        foreach (var f in Positions)
        {
            ct.ThrowIfCancellationRequested();
            var x = await ThumbAsync(tools, a, lengthSeconds * f, videoIndexA, ct).ConfigureAwait(false);
            if (x is null) return PictureMatch.Unknown;
            var y = await ThumbAsync(tools, b, lengthSeconds * f, videoIndexB, ct).ConfigureAwait(false);
            if (y is null) return PictureMatch.Unknown;
            var (same, proof) = Compare(x, y);
            if (!same) return PictureMatch.Different;
            if (proof) informative++;
        }
        return informative >= 2 ? PictureMatch.Same : PictureMatch.Unknown;
    }
}
