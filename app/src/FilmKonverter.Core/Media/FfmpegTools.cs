using System.Text.RegularExpressions;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public sealed partial class FfmpegTools
{
    public string Folder { get; }
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public string Version { get; private set; } = "";
    public HashSet<string> Encoders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Bsfs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Major
    {
        get
        {
            if (Version.StartsWith("N-", StringComparison.Ordinal)) return 99;
            var digits = new string(Version.TakeWhile(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, out var m) ? m : 0;
        }
    }

    public bool CanStripDolbyVision => Bsfs.Contains("dovi_rpu");

    public static string NullTarget => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private FfmpegTools(string folder)
    {
        Folder = folder;
        FfmpegPath = Path.Combine(folder, ExeName("ffmpeg"));
        FfprobePath = Path.Combine(folder, ExeName("ffprobe"));
    }

    public static async Task<FfmpegTools?> TryLoadAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var t = new FfmpegTools(folder);
        if (!File.Exists(t.FfmpegPath) || !File.Exists(t.FfprobePath)) return null;
        try
        {
            var ver = await ProcessRunner.RunAsync(t.FfmpegPath, ["-hide_banner", "-version"], TimeSpan.FromSeconds(20), ct);
            var m = VersionRegex().Match(ver.StdOut);
            t.Version = m.Success ? m.Groups[1].Value : L.T("unbekannt", "unknown");
            var enc = await ProcessRunner.RunAsync(t.FfmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct);
            foreach (var line in enc.StdOut.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 6) t.Encoders.Add(parts[1]);
            }
            try
            {
                var bsf = await ProcessRunner.RunAsync(t.FfmpegPath, ["-hide_banner", "-bsfs"], TimeSpan.FromSeconds(20), ct);
                foreach (var line in bsf.StdOut.Split('\n').Select(l => l.Trim()))
                    if (line.Length > 0 && !line.Contains(' ') && !line.EndsWith(':')) t.Bsfs.Add(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            return t;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionRegex();

    public static string EncoderName(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "hevc_nvenc",
        EncoderKind.Amf => "hevc_amf",
        EncoderKind.Qsv => "hevc_qsv",
        _ => "libx265",
    };

    public bool HasEncoder(EncoderKind k) => Encoders.Contains(EncoderName(k));

    public async Task<(bool Ok, string Message)> TestEncoderAsync(EncoderKind k, CancellationToken ct = default)
    {
        var pix = k == EncoderKind.Amf ? "nv12" : "yuv420p";
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(FfmpegPath,
                ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=black:s=640x360:d=0.5",
                 "-pix_fmt", pix, "-c:v", EncoderName(k), "-f", "null", NullTarget],
                TimeSpan.FromSeconds(60), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
        var err = r.StdErr.Trim();
        return r.ExitCode == 0 ? (true, L.T("funktioniert", "works")) : (false, string.IsNullOrEmpty(err) ? $"Exit-Code {r.ExitCode}" : err.Split('\n').Last().Trim());
    }

    public async Task<(bool Ok, string Message)> TestCodecAsync(string encoder, CancellationToken ct = default, bool tenBit = false)
    {
        var cpu = encoder.StartsWith("lib", StringComparison.Ordinal);
        var pix = tenBit ? (cpu ? "yuv420p10le" : "p010le")
            : encoder.EndsWith("_amf", StringComparison.Ordinal) || encoder.EndsWith("_qsv", StringComparison.Ordinal) ? "nv12" : "yuv420p";
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=black:s=640x360:d=0.5", "-pix_fmt", pix, "-c:v", encoder];
        if (tenBit && (encoder.StartsWith("hevc", StringComparison.Ordinal) || encoder == "libx265")) args.AddRange(["-profile:v", "main10"]);
        args.AddRange(["-f", "null", NullTarget]);
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(FfmpegPath, args, TimeSpan.FromSeconds(60), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
        var err = r.StdErr.Trim();
        return r.ExitCode == 0 ? (true, "") : (false, string.IsNullOrEmpty(err) ? $"Exit-Code {r.ExitCode}" : err.Split('\n').Last().Trim());
    }

    public async Task<(bool Ok, string Message)> TestProfileAsync(Profile p, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gimbo-test-" + Guid.NewGuid().ToString("N")[..8] + ".mkv");
        try
        {
            var depth = p.Encoder == EncoderKind.Amf ? 8 : p.TenBit ? 10 : 8;
            var pix = depth == 10 ? (p.Encoder == EncoderKind.Cpu ? "yuv420p10le" : "p010le") : (p.Encoder == EncoderKind.Qsv ? "nv12" : "yuv420p");
            var video = new StreamInfo { Index = 0, CodecType = "video", CodecName = "rawvideo", Width = 640, Height = 360, PixFmt = "yuv420p" };
            var plan = new Encoding.EncodePlan(video, p.Encoder == EncoderKind.Amf ? "format=nv12" : null, depth, pix, depth == 10 ? "main10" : "main",
                new Encoding.AudioPlan([]), "", "");
            var test = p.Clone();
            test.HardwareDecode = false;
            var args = Encoding.ArgsBuilder.BuildEncode(test, plan, "testsrc2=size=640x360:rate=24", tmp, ["-f", "lavfi", "-t", "1"]);
            args.InsertRange(0, ["-v", "error", "-nostdin"]);
            var r = await ProcessRunner.RunAsync(FfmpegPath, args, TimeSpan.FromMinutes(1), ct);
            var err = r.StdErr.Trim();
            return r.ExitCode == 0 ? (true, "") : (false, string.IsNullOrEmpty(err) ? $"Exit-Code {r.ExitCode}" : err.Split('\n').Last().Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public async Task<long> CountVideoPacketsAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file],
            TimeSpan.FromMinutes(30), ct);
        return long.TryParse(r.StdOut.Trim().TrimEnd(','), out var n) ? n : -1;
    }

    public async Task<MediaInfo?> ProbeAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(FfprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", file],
            TimeSpan.FromMinutes(2), ct);
        if (string.IsNullOrWhiteSpace(r.StdOut)) return null;
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        try
        {
            var info = MediaInfo.Parse(file, r.StdOut, size);
            return info.Streams.Count == 0 && info.DurationSeconds <= 0 ? null : info;
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<string> FindCandidateFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Raw()
        {
            if (OperatingSystem.IsWindows())
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                yield return Path.Combine(pf, "Jellyfin", "Server");
                yield return Path.Combine(pf, "ffmpeg", "bin");
                yield return @"C:\ffmpeg\bin";
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(winget))
                {
                    foreach (var pkg in SafeDirs(winget).Where(d => System.IO.Path.GetFileName(d).StartsWith("Gyan.FFmpeg", StringComparison.OrdinalIgnoreCase)))
                        foreach (var sub in SafeDirs(pkg))
                            yield return Path.Combine(sub, "bin");
                }
            }
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(p)) yield return p.Trim();
        }
        foreach (var d in Raw())
        {
            if (!seen.Add(d)) continue;
            if (File.Exists(Path.Combine(d, ExeName("ffmpeg"))) && File.Exists(Path.Combine(d, ExeName("ffprobe"))))
                yield return d;
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }
}
