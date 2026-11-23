using System.Globalization;
using System.Text.RegularExpressions;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public sealed record SsimResult(double Average, double Worst, int Samples)
{
    public string Text => L.T(
        $"SSIM {Average.ToString("0.000", Fmt.De)} (schlechteste Stelle {Worst.ToString("0.000", Fmt.De)}, {Samples} Stichproben)",
        $"SSIM {Average.ToString("0.000", Fmt.De)} (worst spot {Worst.ToString("0.000", Fmt.De)}, {Samples} samples)");
}

public static partial class QualityCheck
{
    [GeneratedRegex(@"All:(?<v>[0-9.]+)")]
    private static partial Regex SsimAll();

    public static async Task<SsimResult?> MeasureAsync(FfmpegTools tools, string source, string output, EncodePlan plan,
        int outWidth, int outHeight, double sourceOffset, double outputDuration, int samples, CancellationToken ct)
    {
        if (plan.VideoCopy || outWidth <= 0 || outHeight <= 0 || outputDuration <= 2) return null;
        samples = Math.Clamp(samples, 1, 10);
        const double len = 5;
        var values = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = Math.Max(0, outputDuration * (i + 1) / (samples + 1) - len / 2);
            if (t + len > outputDuration) t = Math.Max(0, outputDuration - len);
            var pre = plan.VideoFilter is { Length: > 0 } f ? f.Replace("format=nv12", "null") + "," : "";
            var graph = string.Create(CultureInfo.InvariantCulture,
                $"[0:{plan.Video.Index}]{pre}scale={outWidth}:{outHeight}:flags=bicubic,format=yuv420p[a];" +
                $"[1:v:0]format=yuv420p[b];[a][b]ssim");
            string[] args =
            [
                "-hide_banner", "-nostdin", "-nostats",
                "-ss", (sourceOffset + t).ToString("0.###", CultureInfo.InvariantCulture), "-t", len.ToString(CultureInfo.InvariantCulture), "-i", source,
                "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-t", len.ToString(CultureInfo.InvariantCulture), "-i", output,
                "-lavfi", graph, "-f", "null", FfmpegTools.NullTarget,
            ];
            var r = await ProcessRunner.RunAsync(tools.FfmpegPath, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var m = SsimAll().Matches(r.StdErr).LastOrDefault();
            if (m is not null && double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                values.Add(v);
        }
        return values.Count == 0 ? null : new SsimResult(values.Average(), values.Min(), values.Count);
    }

    public static async Task<bool> ExtractFrameAsync(FfmpegTools tools, string input, double seconds, int width, int height, string outPng, CancellationToken ct)
    {
        var vf = width > 0 && height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"bwdif=deint=interlaced,scale={width}:{height}:flags=lanczos,setsar=1")
            : "bwdif=deint=interlaced,scale=iw*sar:ih,setsar=1";
        string[] args =
        [
            "-hide_banner", "-v", "error", "-nostdin", "-y",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
            "-map", "0:v:0", "-frames:v", "1", "-vf", vf, outPng,
        ];
        var r = await ProcessRunner.RunAsync(tools.FfmpegPath, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return r.ExitCode == 0 && File.Exists(outPng);
    }
}
