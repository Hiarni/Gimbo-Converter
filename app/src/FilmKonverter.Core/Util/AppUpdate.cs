using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmKonverter.Core.Util;

public sealed record AppRelease(Version Version, string Source, Uri? ZipUrl = null, string? LocalFile = null, string? Sha256 = null)
{
    public string VersionText => Version.ToString(3);
}

public static partial class AppUpdate
{
    public const string Repo = "Hiarni/Gimbo-Konverter";
    public const string ExeName = "Gimbo Konverter.exe";
    public static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repo}/releases/latest");

    [GeneratedRegex(@"v?(?<v>\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRx();

    public static Version? ParseVersion(string? text)
    {
        var m = VersionRx().Match(text ?? "");
        return m.Success && Version.TryParse(m.Groups["v"].Value, out var v) ? Norm(v) : null;
    }

    public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GimboKonverter");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static async Task<AppRelease?> CheckGitHubAsync(HttpClient http, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(LatestUrl, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(L.T("Auf GitHub ist keine Version abrufbar (Seite nicht öffentlich oder noch kein Release).",
                                                    "No version can be fetched from GitHub (page not public or no release yet)."));
        resp.EnsureSuccessStatusCode();
        return ParseGitHub(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static AppRelease? ParseGitHub(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var version = ParseVersion(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
        if (version is null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            var digest = a.TryGetProperty("digest", out var g) ? g.GetString() : null;
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            return new AppRelease(version, "GitHub", ZipUrl: uri, Sha256: sha);
        }
        return null;
    }

    public static AppRelease? CheckFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var found = new List<AppRelease>();
        foreach (var zip in Directory.EnumerateFiles(folder, "Gimbo-Konverter*.zip"))
            if (ParseVersion(Path.GetFileNameWithoutExtension(zip)) is { } v) found.Add(new AppRelease(v, folder, LocalFile: zip));
        var exe = Path.Combine(folder, ExeName);
        if (File.Exists(exe) && ExeVersion(exe) is { } ev) found.Add(new AppRelease(ev, folder, LocalFile: exe));
        return found.OrderByDescending(r => r.Version).FirstOrDefault();
    }

    public static Version? ExeVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return ParseVersion(info.ProductVersion) ?? ParseVersion(info.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    public static AppRelease? NewerThan(Version current, params AppRelease?[] found) =>
        found.Where(r => r is not null && r.Version > Norm(current)).OrderByDescending(r => r!.Version).FirstOrDefault();

    public static async Task<string> PrepareAsync(HttpClient? http, AppRelease release, string exePath, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<string>? detail = null)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var newExe = exePath + ".new";
        var ext = release.ZipUrl is { } z && !z.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".exe" : ".zip";
        var download = Path.Combine(dir, $".gimbo-update-{Guid.NewGuid().ToString("N")[..6]}{ext}");
        try
        {
            string source;
            if (release.LocalFile is { } local)
            {
                source = local;
            }
            else if (release.ZipUrl is { } url && http is not null)
            {
                await Download.DownloadAsync(http, url, download, st =>
                {
                    if (st.Fraction >= 0) progress?.Report(Math.Min(0.9, st.Fraction * 0.9));
                    detail?.Report(st.Text);
                }, ct: ct).ConfigureAwait(false);
                source = download;
            }
            else
            {
                throw new InvalidOperationException(L.T("Keine Quelle für das Update.", "No source for the update."));
            }

            if (release.Sha256 is { Length: 64 } expected)
            {
                string actual;
                await using (var fs = File.OpenRead(source)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != expected)
                    throw new InvalidOperationException(L.T("Das heruntergeladene Update ist beschädigt (Prüfsumme stimmt nicht). Es wurde nichts geändert.",
                                                            "The downloaded update is damaged (checksum mismatch). Nothing was changed."));
            }

            if (File.Exists(newExe)) File.Delete(newExe);
            if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(source);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException(L.T("Im Update fehlt das Programm.", "The update does not contain the program."));
                entry.ExtractToFile(newExe);
            }
            else
            {
                File.Copy(source, newExe);
            }

            CheckExe(newExe);
            progress?.Report(1);
            return newExe;
        }
        catch
        {
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
            throw;
        }
        finally
        {
            try { if (File.Exists(download)) File.Delete(download); } catch { }
        }
    }

    public static void CheckExe(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length < 1_000_000)
            throw new InvalidOperationException(L.T("Das Update ist unvollständig.", "The update is incomplete."));
        using var fs = fi.OpenRead();
        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            throw new InvalidOperationException(L.T("Das Update ist kein Windows-Programm.", "The update is not a Windows program."));
    }

    public static void Swap(string exePath, string newExe)
    {
        var old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newExe, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    public static void CleanupOld(string exePath)
    {
        foreach (var f in new[] { exePath + ".old", exePath + ".new" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    public static bool CanWrite(string exePath)
    {
        try
        {
            var test = Path.Combine(Path.GetDirectoryName(exePath)!, $".gimbo-schreibtest-{Guid.NewGuid().ToString("N")[..6]}");
            File.WriteAllText(test, "");
            File.Delete(test);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
