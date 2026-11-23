namespace FilmKonverter.Core.Util;

public static class FullscreenRules
{
    public static readonly IReadOnlySet<string> Harmless = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "msrdcw", "RdClient.Windows", "Windows365", "vmconnect", "RDCMan", "mRemoteNG", "RemoteDesktopManager", "RoyalTS",
        "AnyDesk", "TeamViewer", "rustdesk", "vncviewer", "tvnviewer", "UltraVNC", "nxplayer.bin", "remoting_desktop", "SupRemo",
        "DWRCC", "Splashtop", "strwinclt", "g2mainh",
        "wfica32", "CDViewer", "vmware-view", "vmware-remotemks", "vmware", "vmplayer", "VirtualBoxVM", "VirtualBox",
    };

    public static string Normalize(string? name)
    {
        var s = (name ?? "").Trim().Trim('"', '\'').Trim();
        var slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim();
    }

    public static IEnumerable<string> Parse(string? list) =>
        (list ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(x => x.Length > 0);

    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    public static string Clean(string? list) => string.Join(", ", Parse(list).Distinct(StringComparer.OrdinalIgnoreCase));

    public static bool IsIgnored(string? process, string? extra)
    {
        var name = Normalize(process);
        if (name.Length == 0) return false;
        return Harmless.Contains(name) || Parse(extra).Any(x => Matches(name, x));
    }

    private static bool Matches(string name, string pattern)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        var start = pattern.StartsWith('*');
        var end = pattern.EndsWith('*');
        var core = pattern.Trim('*');
        if (core.Length == 0) return false;
        if (start && end) return name.Contains(core, ic);
        if (start) return name.EndsWith(core, ic);
        if (end) return name.StartsWith(core, ic);
        return name.Equals(core, ic);
    }
}
