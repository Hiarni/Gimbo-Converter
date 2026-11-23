using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public sealed record FfmpegRelease(string Version, Uri ZipUrl, Uri ShaUrl)
{
    public string? Sha256 { get; init; }

    public string Source { get; init; } = "gyan.dev";

    public Version? Parsed => FfmpegLocator.ParseVersion(Version);
}

public static partial class FfmpegUpdate
{
    public static readonly Uri VersionUrl = new("https://www.gyan.dev/ffmpeg/builds/release-version");
    public static readonly Uri ZipUrl = new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
    public static readonly Uri ShaUrl = new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256");

    public const string MirrorRepo = "GyanD/codexffmpeg";

    public static readonly string[] WantedEncoders = ["hevc_nvenc", "hevc_amf", "hevc_qsv", "libx265", "h264_nvenc", "h264_amf", "h264_qsv", "libx264", "av1_nvenc", "av1_amf", "av1_qsv", "libmp3lame", "aac", "ac3", "eac3", "flac"];
    public static readonly string[] WantedFilters = ["scale", "zscale", "tonemap", "bwdif", "ssim"];
    public static readonly string[] WantedBsfs = ["dovi_rpu", "hevc_metadata", "h264_metadata"];

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Rx();

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GimboKonverter");
        return http;
    }

    public static async Task<FfmpegRelease?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        var release = ParseRelease(await http.GetStringAsync(VersionUrl, ct).ConfigureAwait(false));
        if (release is null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{MirrorRepo}/releases/tags/{Uri.EscapeDataString(release.Version)}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && ParseMirror(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), release) is { } mirror)
                return mirror;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
        return release;
    }

    public static FfmpegRelease? ParseMirror(string json, FfmpegRelease release)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith("-essentials_build.zip", StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71) continue;
                return release with { ZipUrl = uri, Sha256 = digest[7..].ToLowerInvariant(), Source = "GitHub" };
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static FfmpegRelease? ParseRelease(string text)
    {
        var v = (text ?? "").Trim().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return v.Length is > 0 and < 40 && FfmpegLocator.ParseVersion(v) is not null ? new FfmpegRelease(v, ZipUrl, ShaUrl) : null;
    }

    private static async Task DownloadCheckedAsync(HttpClient http, FfmpegRelease release, string zipFile, IProgress<double>? progress, IProgress<string>? detail, CancellationToken ct)
    {
        var expected = release.Sha256;
        if (expected is null)
        {
            var shaText = await http.GetStringAsync(release.ShaUrl, ct).ConfigureAwait(false);
            expected = Sha256Rx().Match(shaText) is { Success: true } m ? m.Value.ToLowerInvariant()
                : throw new InvalidOperationException(L.T("Die Prüfsumme der Quelle ist nicht lesbar.", "The checksum of the source is not readable."));
        }
        await Download.DownloadAsync(http, release.ZipUrl, zipFile, s =>
        {
            if (s.Fraction >= 0) progress?.Report(Math.Min(0.95, s.Fraction * 0.95));
            detail?.Report(s.Text);
        }, ct: ct).ConfigureAwait(false);

        string actual;
        await using (var fs = File.OpenRead(zipFile)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidOperationException(L.T("Die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht). Es wurde nichts geändert.",
                                                    "The downloaded file is damaged (checksum mismatch). Nothing was changed."));
    }

    public static bool IsNewer(FfmpegRelease release, string? current) =>
        release.Parsed is { } r && (FfmpegLocator.ParseVersion(current) is not { } c || r > c);

    public static async Task<string> InstallAsync(HttpClient http, FfmpegRelease release, string root, FfmpegTools? reference,
        IProgress<double>? progress = null, CancellationToken ct = default, IProgress<string>? detail = null)
    {
        Directory.CreateDirectory(root);
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var zipFile = Path.Combine(root, $"download-{stamp}.zip");
        var temp = Path.Combine(root, $"{Safe(release.Version)}.tmp-{stamp}");
        try
        {
            try
            {
                await DownloadCheckedAsync(http, release, zipFile, progress, detail, ct).ConfigureAwait(false);
            }
            catch (Exception) when (release.Source != "gyan.dev" && !ct.IsCancellationRequested)
            {
                detail?.Report(L.T("Spiegel nicht erreichbar, lade von gyan.dev …", "Mirror not reachable, downloading from gyan.dev …"));
                await DownloadCheckedAsync(http, release with { ZipUrl = ZipUrl, Sha256 = null, Source = "gyan.dev" }, zipFile, progress, detail, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(temp);
            using (var zip = ZipFile.OpenRead(zipFile))
            {
                foreach (var name in new[] { "ffmpeg", "ffprobe" })
                {
                    var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)
                                                                && e.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                                ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException(L.T($"{exe} fehlt im Paket.", $"{exe} is missing in the package."));
                    var dest = Path.Combine(temp, exe);
                    entry.ExtractToFile(dest, overwrite: true);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(temp, "version.txt"), release.Version, ct).ConfigureAwait(false);

            var tools = await FfmpegTools.TryLoadAsync(temp, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(L.T("Die neue Fassung startet nicht. Es wurde nichts geändert.", "The new version does not start. Nothing was changed."));
            var missing = await MissingAsync(tools, reference, ct).ConfigureAwait(false);
            if (missing.Count > 0)
                throw new InvalidOperationException(L.T($"Der neuen Fassung fehlt, was Gimbo braucht: {string.Join(", ", missing)}. Es bleibt die bisherige.",
                                                        $"The new version lacks what Gimbo needs: {string.Join(", ", missing)}. The current one stays."));

            var target = Path.Combine(root, Safe(release.Version));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(temp, target);
            progress?.Report(1);
            return target;
        }
        finally
        {
            try { if (File.Exists(zipFile)) File.Delete(zipFile); } catch { }
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    public static async Task<List<string>> MissingAsync(FfmpegTools candidate, FfmpegTools? reference, CancellationToken ct = default)
    {
        var missing = new List<string>();
        var refEnc = reference?.Encoders ?? new HashSet<string>(["libx265", "libx264"], StringComparer.OrdinalIgnoreCase);
        missing.AddRange(WantedEncoders.Where(e => refEnc.Contains(e) && !candidate.Encoders.Contains(e)));
        var refBsf = reference?.Bsfs ?? new HashSet<string>(["hevc_metadata"], StringComparer.OrdinalIgnoreCase);
        missing.AddRange(WantedBsfs.Where(b => refBsf.Contains(b) && !candidate.Bsfs.Contains(b)));
        var candFilters = await FiltersAsync(candidate, ct).ConfigureAwait(false);
        var refFilters = reference is null ? new HashSet<string>(["scale"], StringComparer.OrdinalIgnoreCase) : await FiltersAsync(reference, ct).ConfigureAwait(false);
        missing.AddRange(WantedFilters.Where(f => refFilters.Contains(f) && !candFilters.Contains(f)));
        return missing;
    }

    private static async Task<HashSet<string>> FiltersAsync(FfmpegTools t, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = await ProcessRunner.RunAsync(t.FfmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            foreach (var line in r.StdOut.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0].Length is >= 2 and <= 4) set.Add(parts[1]);
            }
        }
        catch { }
        return set;
    }

    public static void Prune(string root, params string?[] keep)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var keepSet = keep.Where(k => !string.IsNullOrEmpty(k)).Select(k => Path.GetFullPath(k!).TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.GetDirectories(root))
            {
                if (keepSet.Contains(Path.GetFullPath(d).TrimEnd('\\', '/'))) continue;
                try { Directory.Delete(d, recursive: true); } catch { }
            }
        }
        catch { }
    }

    private static string Safe(string version)
    {
        var s = string.Concat(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        return s.Length == 0 ? "ffmpeg" : s;
    }
}
