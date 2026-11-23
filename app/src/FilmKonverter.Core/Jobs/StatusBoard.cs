using System.Text.Json;
using FilmKonverter.Core.Library;

namespace FilmKonverter.Core.Jobs;

public enum PeerState
{
    Idle,
    Running,
    Paused,
    Waiting,
}

public sealed class PeerFile
{
    public string Name { get; set; } = "";
    public double Percent { get; set; }
    public double Speed { get; set; }
    public double EtaSeconds { get; set; } = -1;
    public string Stage { get; set; } = "";
}

public sealed class PeerStatus
{
    public string Machine { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public PeerState State { get; set; }
    public string Profile { get; set; } = "";
    public string Note { get; set; } = "";
    public int Done { get; set; }
    public int Total { get; set; }
    public int Encoded { get; set; }
    public int Errors { get; set; }
    public long GainBytes { get; set; }
    public string Version { get; set; } = "";
    public List<PeerFile> Files { get; set; } = [];

    public bool IsFresh(DateTime nowUtc) => nowUtc - UpdatedUtc < StatusBoard.FreshFor;

    public bool IsActive(DateTime nowUtc) => IsFresh(nowUtc) && State != PeerState.Idle;

    public bool IsWorthShowing(DateTime nowUtc) => IsActive(nowUtc) || nowUtc - UpdatedUtc < StatusBoard.ShowAfterFor;
}

public static class StatusBoard
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan ShowAfterFor = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder(string outputRoot) => DataStore.Status(outputRoot);

    public static string FileFor(string outputRoot, string machine) => Path.Combine(Folder(outputRoot), Safe(machine) + ".json");

    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    public static bool Write(string outputRoot, PeerStatus status)
    {
        if (string.IsNullOrWhiteSpace(outputRoot)) return false;
        try
        {
            Directory.CreateDirectory(Folder(outputRoot));
            var file = FileFor(outputRoot, status.Machine);
            var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(status, Json));
                File.Move(tmp, file, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<PeerStatus> ReadOthers(string outputRoot, string ownMachine)
    {
        var list = new List<PeerStatus>();
        try
        {
            var dir = Folder(outputRoot);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<PeerStatus>(File.ReadAllText(f), Json);
                    if (s is null || string.IsNullOrEmpty(s.Machine)) continue;
                    if (s.Machine.Equals(ownMachine, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(s);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return list.OrderByDescending(s => s.UpdatedUtc).ToList();
    }
}
