using System.IO.Compression;
using System.Text.RegularExpressions;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public enum FfmpegMode
{
    Auto,
    Bundled,
    Custom,
}

public sealed record FfmpegCandidate(FfmpegTools Tools, bool Bundled)
{
    public Version? ParsedVersion => FfmpegLocator.ParseVersion(Tools.Version);
}

public static partial class FfmpegLocator
{
    [GeneratedRegex(@"^n?(?<a>\d+)\.(?<b>\d+)(?:\.(?<c>\d+))?")]
    private static partial Regex VersionRx();

    public static Version? ParseVersion(string? text)
    {
        var m = VersionRx().Match((text ?? "").Trim());
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups["a"].Value), int.Parse(m.Groups["b"].Value), m.Groups["c"].Success ? int.Parse(m.Groups["c"].Value) : 0);
    }

    public static FfmpegCandidate? PickNewest(IEnumerable<FfmpegCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.ParsedVersion ?? new Version(0, 0))
            .ThenByDescending(c => c.Bundled)
            .FirstOrDefault();

    public static FfmpegCandidate? PickAuto(IEnumerable<FfmpegCandidate> candidates, Settings.EncoderKind? encoder)
    {
        var list = candidates.ToList();
        if (encoder is not { } needed) return PickNewest(list);
        return PickNewest(list.Where(c => c.Tools.HasEncoder(needed)))
               ?? list.FirstOrDefault(c => c.Bundled)
               ?? PickNewest(list);
    }

    public static async Task<List<FfmpegCandidate>> FindAllAsync(string? bundledFolder, CancellationToken ct = default)
    {
        var list = new List<FfmpegCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(bundledFolder) && await FfmpegTools.TryLoadAsync(bundledFolder, ct) is { } b)
        {
            list.Add(new FfmpegCandidate(b, true));
            seen.Add(Path.GetFullPath(bundledFolder));
        }
        foreach (var dir in FfmpegTools.FindCandidateFolders())
        {
            string full;
            try { full = Path.GetFullPath(dir); } catch { continue; }
            if (!seen.Add(full)) continue;
            if (await FfmpegTools.TryLoadAsync(dir, ct) is { } t) list.Add(new FfmpegCandidate(t, false));
        }
        return list;
    }
}

public static class BundledFfmpeg
{
    public const string ResourceName = "ffmpeg-bundle.zip";

    public static string? EnsureExtracted(Func<Stream?> openBundle, string root)
    {
        using var stream = openBundle();
        if (stream is null) return null;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var versionEntry = zip.GetEntry("version.txt");
        var version = versionEntry is null ? "unbekannt" : new StreamReader(versionEntry.Open()).ReadToEnd().Trim();
        var safe = string.Concat(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        if (safe.Length == 0) safe = "ffmpeg";
        var target = Path.Combine(root, safe);

        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        if (files.All(e => File.Exists(Path.Combine(target, e.Name)) && new FileInfo(Path.Combine(target, e.Name)).Length == e.Length))
            return target;

        Directory.CreateDirectory(root);
        var temp = Path.Combine(root, safe + ".tmp-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var e in files)
            {
                var dest = Path.Combine(temp, e.Name);
                e.ExtractToFile(dest, overwrite: true);
                if (!OperatingSystem.IsWindows() && e.Name is "ffmpeg" or "ffprobe")
                    File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(temp, target);
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            if (files.All(e => File.Exists(Path.Combine(target, e.Name)))) return target;
            throw;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (string.Equals(dir, target, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return target;
    }
}
