using System.Text.Json;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using Xunit;

namespace FilmKonverter.Core.Tests;

public class DataStoreTests
{
    private static (string Source, string Target) NewLib()
    {
        var root = Path.Combine(Path.GetTempPath(), "gimbo-ds-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "Serien");
        var target = Path.Combine(root, "Komprimiert");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        DataStore.Link(target, source);
        return (source, target);
    }

    private static StatsRecord Rec(string file, DateTime? utc = null, string machine = "PC-A") => new()
    {
        TimeUtc = utc ?? DateTime.UtcNow, Machine = machine, File = file, Outcome = "Encoded", SourceBytes = 100, OutputBytes = 40, GainBytes = 60,
    };

    private static string Line(StatsRecord r) => JsonSerializer.Serialize(r) + "\n";

    private static LibraryPaths Paths((string Source, string Target) lib, params string[] rel) =>
        new(lib.Source, lib.Target, Path.Combine([lib.Source, .. rel]));

    private static string[] Entries(string dir) =>
        Directory.GetFileSystemEntries(dir).Select(e => Path.GetFileName(e)!).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Im_Zielordner_landet_nichts_alles_liegt_versteckt_im_Quellordner()
    {
        var lib = NewLib();
        MarkList.Mark(Paths(lib, "Serie", "Folge.mkv"), "G95", "ueber der Grenze", "CQ 27", 95);
        MarkList.MarkVerified(lib.Target, Path.Combine(lib.Target, "Serie", "Folge.mkv"), 123, "Test");
        StatsLog.Append(lib.Target, Rec("Serie/Folge.mkv"));
        StatusBoard.Write(lib.Target, new PeerStatus { Machine = "PC-A", UpdatedUtc = DateTime.UtcNow });
        WorkQueue.Add(lib.Target, lib.Source, [Path.Combine(lib.Source, "Serie")], "PC-A");
        DataStore.Sync(lib.Target);

        Assert.Empty(Directory.GetFileSystemEntries(lib.Target));
        var hidden = Path.Combine(lib.Source, ".gimbo");
        var store = Path.Combine(hidden, "metadata");
        Assert.Equal(SharingState.Shared, DataStore.State(lib.Target));
        Assert.True(File.Exists(Path.Combine(store, "Merkliste", "Serie", "Folge.G95.txt")));
        Assert.True(File.Exists(Path.Combine(store, "Statistik", "PC-A.jsonl")));
        Assert.True(File.Exists(Path.Combine(store, "Status", "PC-A.json")));
        Assert.True(File.Exists(Path.Combine(store, "Warteschlange", "queue.json")));
        Assert.True(Directory.Exists(Path.Combine(store, "Pruefung")));
        Assert.True(File.Exists(Path.Combine(hidden, ".ignore")));
        Assert.True(File.Exists(Path.Combine(hidden, ".plexignore")));
        Assert.True(File.Exists(Path.Combine(hidden, ".nomedia")));
        Assert.Equal([".ignore", ".nomedia", ".plexignore", "metadata"], Entries(hidden));
        Assert.Equal([".gimbo"], Entries(lib.Source));
    }

    [Fact]
    public void Zielordner_loeschen_aendert_nichts_an_Listen_und_Statistik()
    {
        var lib = NewLib();
        var p = Paths(lib, "Serie", "Folge.mkv");
        MarkList.Mark(p, "G95", "ueber der Grenze", "CQ 27", 95);
        StatsLog.Append(lib.Target, Rec("Serie/Folge.mkv"));
        WorkQueue.Add(lib.Target, lib.Source, [Path.Combine(lib.Source, "Serie")], "PC-A");

        Directory.Delete(lib.Target, recursive: true);

        Assert.True(MarkList.IsMarked(p, "G95"));
        Assert.Single(StatsLog.ReadAll([lib.Target]));
        Assert.Single(WorkQueue.Read(lib.Target));
        Assert.False(DataStore.Sync(lib.Target).Restored);
        Assert.False(Directory.Exists(lib.Target));
    }

    [Fact]
    public void Verschwindet_der_gemeinsame_Ordner_kommt_alles_aus_der_Sicherung_zurueck()
    {
        var lib = NewLib();
        var p = Paths(lib, "Serie", "Folge.mkv");
        DataStore.Sync(lib.Target);
        MarkList.Mark(p, "G95", "ueber der Grenze", "CQ 27", 95);
        MarkList.MarkOk(Paths(lib, "Serie", "Zwei.mkv"), "G95", 100, 40);
        StatsLog.Append(lib.Target, Rec("Serie/Folge.mkv"));
        StatsLog.Append(lib.Target, Rec("Serie/Zwei.mkv"));

        Directory.Delete(Path.Combine(lib.Source, ".gimbo"), recursive: true);
        DataStore.ResetUsable();
        Assert.False(MarkList.IsMarked(Paths(lib, "Serie", "Folge.mkv"), "G95"));

        var r = DataStore.Sync(lib.Target);
        Assert.True(r.Restored);
        Assert.Equal(2, r.MarksRestored);
        Assert.Equal(1, r.LogsRestored);
        Assert.True(MarkList.IsMarked(Paths(lib, "Serie", "Folge.mkv"), "G95"));
        Assert.True(MarkList.IsOk(Paths(lib, "Serie", "Zwei.mkv"), "G95", 100, 40));
        Assert.Equal(2, StatsLog.ReadAll([lib.Target]).Count);
    }

    [Fact]
    public void Nicht_erreichbare_Quelle_ist_kein_geloeschter_Ordner()
    {
        var lib = NewLib();
        var p = Paths(lib, "Serie", "Folge.mkv");
        MarkList.Mark(p, "G95", "x", "CQ 27", 95);
        StatsLog.Append(lib.Target, Rec("a.mkv"));
        DataStore.Sync(lib.Target);

        var away = lib.Source + "-weg";
        Directory.Move(lib.Source, away);
        DataStore.ResetUsable();
        Assert.Equal(SharingState.NotWritable, DataStore.State(lib.Target));
        Assert.Equal(SyncResult.Nothing, DataStore.Sync(lib.Target));
        Assert.False(Directory.Exists(lib.Source));
        Assert.True(MarkList.IsMarked(Paths(lib, "Serie", "Folge.mkv"), "G95"));
        Assert.Single(StatsLog.ReadAll([lib.Target]));

        Directory.Move(away, lib.Source);
        DataStore.ResetUsable();
        Assert.Equal(SharingState.Shared, DataStore.State(lib.Target));
        Assert.False(DataStore.Sync(lib.Target).Restored);
        Assert.Single(StatsLog.ReadAll([lib.Target]));
    }

    [Fact]
    public void Statistik_ohne_Netzwerk_wird_gesichert_und_spaeter_nachgetragen()
    {
        var lib = NewLib();
        StatsLog.Append(lib.Target, Rec("a.mkv"));
        DataStore.Sync(lib.Target);
        var shared = Path.Combine(DataStore.Stats(lib.Target), "PC-A.jsonl");
        using (new FileStream(shared, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(StatsLog.Append(lib.Target, Rec("b.mkv")));
        Assert.Single(StatsLog.ReadAll([lib.Target]));
        DataStore.Sync(lib.Target);
        Assert.Equal(2, StatsLog.ReadAll([lib.Target]).Count);
    }

    [Fact]
    public void Geloeschter_Vermerk_verschwindet_auch_aus_der_Sicherung()
    {
        var lib = NewLib();
        var p = Paths(lib, "Serie", "Folge.mkv");
        DataStore.Sync(lib.Target);
        MarkList.Mark(p, "G95", "x", "CQ 27", 95);
        Assert.Equal(1, MarkList.Clear(lib.Target, Path.Combine(lib.Target, "Serie", "Folge.mkv")));
        Directory.Delete(Path.Combine(lib.Source, ".gimbo"), recursive: true);
        DataStore.ResetUsable();
        DataStore.Sync(lib.Target);
        Assert.False(MarkList.IsMarked(Paths(lib, "Serie", "Folge.mkv"), "G95"));
    }

    [Fact]
    public void Vermerke_anderer_PCs_werden_beim_Abgleich_gesichert_und_Geloeschtes_entfernt()
    {
        var lib = NewLib();
        DataStore.Sync(lib.Target);
        var foreign = Path.Combine(DataStore.Marks(lib.Target), "Serie", "Fremd.G95.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "von PC-B");
        DataStore.Sync(lib.Target, force: true);
        var backup = Path.Combine(DataStore.LocalRoot(lib.Target), "Merkliste", "Serie", "Fremd.G95.txt");
        Assert.True(File.Exists(backup));
        File.Delete(foreign);
        DataStore.Sync(lib.Target, force: true);
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Zuruecksetzen_gilt_fuer_alle_PCs_auch_nach_dem_Zurueckspielen()
    {
        var lib = NewLib();
        var old = DateTime.UtcNow.AddDays(-3);
        StatsLog.Append(lib.Target, Rec("alt.mkv", old));
        DataStore.Sync(lib.Target);
        File.WriteAllText(Path.Combine(DataStore.Stats(lib.Target), "PC-B.jsonl"), Line(Rec("b-alt.mkv", old, "PC-B")));
        DataStore.Sync(lib.Target);
        Assert.Equal(2, StatsLog.ReadAll([lib.Target]).Count);

        Assert.Equal(2, StatsLog.Archive([lib.Target]));
        Assert.Empty(StatsLog.ReadAll([lib.Target]));
        StatsLog.Append(lib.Target, Rec("neu.mkv"));

        Directory.Delete(Path.Combine(lib.Source, ".gimbo"), recursive: true);
        DataStore.ResetUsable();
        DataStore.Sync(lib.Target);
        Assert.Equal(["neu.mkv"], StatsLog.ReadAll([lib.Target]).Select(r => r.File).ToArray());

        File.WriteAllText(Path.Combine(DataStore.Stats(lib.Target), "PC-C.jsonl"), Line(Rec("c-alt.mkv", old, "PC-C")));
        Assert.Equal(["neu.mkv"], StatsLog.ReadAll([lib.Target]).Select(r => r.File).ToArray());
    }

    [Fact]
    public void Alte_Ordner_im_Zielordner_werden_uebernommen_danach_liegen_dort_nur_Videos()
    {
        var lib = NewLib();
        var t = lib.Target;
        Directory.CreateDirectory(Path.Combine(t, "_Merkliste", "Serie"));
        File.WriteAllText(Path.Combine(t, "_Merkliste", "Serie", "Folge.QP22-24_G95.txt"), "Fruehabbruch");
        Directory.CreateDirectory(Path.Combine(t, "_Pruefung", "Serie"));
        File.WriteAllText(Path.Combine(t, "_Pruefung", "Serie", "Folge.mkv.5.ok"), "x");
        Directory.CreateDirectory(Path.Combine(t, "_Statistik", "Archiv", "2026-01-01_00-00-00"));
        File.WriteAllText(Path.Combine(t, "_Statistik", "PC-A.jsonl"), Line(Rec("a.mkv")));
        File.WriteAllText(Path.Combine(t, "_Statistik", "Archiv", "2026-01-01_00-00-00", "PC-A.jsonl"), "{}\n");
        Directory.CreateDirectory(Path.Combine(t, "_Warteschlange"));
        File.WriteAllText(Path.Combine(t, "_Warteschlange", "queue.json"), "[]");
        File.SetLastWriteTimeUtc(Path.Combine(t, "_Warteschlange", "queue.json"), DateTime.UtcNow.AddDays(-2));
        Directory.CreateDirectory(Path.Combine(t, "_Status"));
        File.WriteAllText(Path.Combine(t, "_Status", "PC-B.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(t, "_Status", "PC-B.json"), DateTime.UtcNow.AddHours(-2));
        Directory.CreateDirectory(Path.Combine(t, "_Test", "Film"));
        File.WriteAllText(Path.Combine(t, "_Test", "Film", "CQ27.mkv"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(t, "_Test", "Film", "CQ27.mkv"), DateTime.UtcNow.AddDays(-3));
        Directory.CreateDirectory(Path.Combine(t, "Serie"));
        File.WriteAllText(Path.Combine(t, "Serie", "Folge.mkv"), "video");

        var r = DataStore.Sync(t);
        Assert.True(r.Imported >= 4);
        Assert.Equal(["Serie"], Entries(t));
        Assert.True(MarkList.IsMarked(Paths(lib, "Serie", "Folge.mkv"), "G95"));
        Assert.True(MarkList.IsVerified(t, Path.Combine(t, "Serie", "Folge.mkv"), 5));
        Assert.Single(StatsLog.ReadAll([t]));
        Assert.True(File.Exists(Path.Combine(DataStore.Stats(t), "Archiv", "2026-01-01_00-00-00", "PC-A.jsonl")));
        Assert.Empty(WorkQueue.Read(t));

        Directory.CreateDirectory(Path.Combine(t, "_Statistik"));
        File.WriteAllText(Path.Combine(t, "_Statistik", "PC-A.jsonl"), Line(Rec("b.mkv")));
        Directory.CreateDirectory(Path.Combine(t, "_Merkliste", "Serie"));
        File.WriteAllText(Path.Combine(t, "_Merkliste", "Serie", "Neu.G95.txt"), "x");
        DataStore.Sync(t);
        Assert.Equal(2, StatsLog.ReadAll([t]).Count);
        Assert.True(MarkList.IsMarked(Paths(lib, "Serie", "Neu.mkv"), "G95"));
        Assert.Equal(["Serie"], Entries(t));
    }

    [Fact]
    public void Der_versteckte_Ordner_wird_beim_Durchsuchen_nie_als_Quelle_gelesen()
    {
        var lib = NewLib();
        Directory.CreateDirectory(Path.Combine(lib.Source, ".gimbo", "metadata", "Merkliste"));
        File.WriteAllText(Path.Combine(lib.Source, ".gimbo", "metadata", "Merkliste", "x.mkv"), "x");
        File.WriteAllText(Path.Combine(lib.Source, "echt.mkv"), "x");
        Assert.Equal(["echt.mkv"], Scanner.Find(lib.Source, [".mkv"]).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Sicherung_haengt_an_der_Quelle_und_mehrere_Quellen_in_ein_Ziel_teilen_sich_einen_Ort()
    {
        var root = Path.Combine(Path.GetTempPath(), "gimbo-ds-" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(root, "Komprimiert");
        var serien = Path.Combine(root, "Serien");
        var filme = Path.Combine(root, "Filme");
        Assert.Null(DataStore.SourceOf(target));
        Assert.Equal(SharingState.NoSource, DataStore.State(target));
        Assert.StartsWith(DataStore.LocalBase, DataStore.Marks(target));

        DataStore.Link(target, serien);
        DataStore.Link(target + Path.DirectorySeparatorChar, serien + Path.DirectorySeparatorChar);
        Assert.Equal(serien, DataStore.SourceOf(target));
        DataStore.Link(target, filme);
        Assert.Equal(filme, DataStore.SourceOf(target));
        Assert.StartsWith("Filme-", Path.GetFileName(DataStore.LocalRoot(target)));
        Assert.StartsWith(DataStore.LocalBase, DataStore.LocalRoot(target));
    }
}

[CollectionDefinition("DataStoreLocal", DisableParallelization = true)]
public class DataStoreLocalCollection;

[Collection("DataStoreLocal")]
public class DataStoreLocalTests
{
    private static (string Source, string Target) NewLib()
    {
        var root = Path.Combine(Path.GetTempPath(), "gimbo-ds-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "Serien");
        var target = Path.Combine(root, "Komprimiert");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        DataStore.Link(target, source);
        return (source, target);
    }

    private static void Legacy(string target, string? otherPcStatus)
    {
        Directory.CreateDirectory(Path.Combine(target, "_Merkliste", "Serie"));
        File.WriteAllText(Path.Combine(target, "_Merkliste", "Serie", "Folge.G95.txt"), "x");
        Directory.CreateDirectory(Path.Combine(target, "_Statistik"));
        File.WriteAllText(Path.Combine(target, "_Statistik", "PC-A.jsonl"),
            JsonSerializer.Serialize(new StatsRecord { TimeUtc = DateTime.UtcNow, Machine = "PC-A", File = "alt.mkv", Outcome = "Encoded" }) + "\n");
        foreach (var f in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
        if (otherPcStatus is null) return;
        Directory.CreateDirectory(Path.Combine(target, "_Status"));
        File.WriteAllText(Path.Combine(target, "_Status", otherPcStatus + ".json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(target, "_Status", otherPcStatus + ".json"), DateTime.UtcNow.AddDays(-2));
    }

    [Fact]
    public void Nur_dieser_PC_weder_im_Ziel_noch_in_der_Quelle_landet_etwas()
    {
        var lib = NewLib();
        Legacy(lib.Target, otherPcStatus: null);
        DataStore.Share = false;
        try
        {
            Assert.Equal(SharingState.Off, DataStore.State(lib.Target));
            DataStore.Sync(lib.Target);
            var p = new LibraryPaths(lib.Source, lib.Target, Path.Combine(lib.Source, "Serie", "Folge.mkv"));
            Assert.True(MarkList.IsMarked(p, "G95"));
            Assert.Single(StatsLog.ReadAll([lib.Target]));

            MarkList.Mark(new LibraryPaths(lib.Source, lib.Target, Path.Combine(lib.Source, "Serie", "Neu.mkv")), "G95", "x", "CQ 27", 95);
            StatsLog.Append(lib.Target, new StatsRecord { TimeUtc = DateTime.UtcNow, Machine = "PC-A", File = "neu.mkv", Outcome = "Encoded" });
            WorkQueue.Add(lib.Target, lib.Source, [Path.Combine(lib.Source, "Serie")], "PC-A");
            Assert.Equal(2, StatsLog.ReadAll([lib.Target]).Count);
            Assert.StartsWith(DataStore.LocalBase, DataStore.Marks(lib.Target));
            Assert.Empty(Directory.GetFileSystemEntries(lib.Target));
            Assert.Empty(Directory.GetFileSystemEntries(lib.Source));
        }
        finally
        {
            DataStore.Share = true;
            DataStore.ResetUsable();
        }
    }

    [Fact]
    public void Nur_dieser_PC_alte_Ordner_bleiben_solange_ein_anderer_PC_sie_noch_braucht()
    {
        var lib = NewLib();
        Legacy(lib.Target, otherPcStatus: "PC-B");
        DataStore.Share = false;
        try
        {
            DataStore.Sync(lib.Target);
            var p = new LibraryPaths(lib.Source, lib.Target, Path.Combine(lib.Source, "Serie", "Folge.mkv"));
            Assert.True(MarkList.IsMarked(p, "G95"));
            Assert.True(File.Exists(Path.Combine(lib.Target, "_Merkliste", "Serie", "Folge.G95.txt")));
            Assert.True(File.Exists(Path.Combine(lib.Target, "_Statistik", "PC-A.jsonl")));
        }
        finally
        {
            DataStore.Share = true;
            DataStore.ResetUsable();
        }
    }
}

public class ErrorMarkPerPcTests
{
    [Fact]
    public void Zwei_Fehlversuche_eines_anderen_PCs_sperren_diesen_PC_nicht()
    {
        var root = Path.Combine(Path.GetTempPath(), "gimbo-ds-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Serien", "S"));
        var p = new LibraryPaths(Path.Combine(root, "Serien"), Path.Combine(root, "Komprimiert"), Path.Combine(root, "Serien", "S", "Folge.mkv"));
        Directory.CreateDirectory(p.MarkDir);
        File.WriteAllLines(ErrorMarks.FileFor(p, 500),
        [
            "1 - 2026-11-01 10:00 - ANDERER-PC - Encoder lässt sich nicht starten",
            "2 - 2026-11-01 11:00 - ANDERER-PC - Encoder lässt sich nicht starten",
        ]);
        var mark = ErrorMarks.Get(p, 500)!;
        Assert.Equal((2, 0, false), (mark.Count, mark.OwnCount, mark.Blocks));

        Assert.Equal(3, ErrorMarks.Add(p, 500, "hier auch"));
        Assert.False(ErrorMarks.Get(p, 500)!.Blocks);
        ErrorMarks.Add(p, 500, "hier wieder");
        mark = ErrorMarks.Get(p, 500)!;
        Assert.Equal((4, 2, true), (mark.Count, mark.OwnCount, mark.Blocks));
    }
}

[Collection("media")]
public sealed class SampleInAnalysisTests(TestMedia m)
{
    [Fact]
    public async Task Sample_neben_dem_Original_steht_bei_den_Doppelten_und_wird_nie_kodiert()
    {
        var root = Path.Combine(m.Dir, "smp-" + Guid.NewGuid().ToString("N")[..6]);
        var dir = Path.Combine(root, "Serien", "Neustart", "Staffel 3");
        Directory.CreateDirectory(dir);
        var original = Path.Combine(dir, "neustart.s03e01.german.dl.1080p.web.h265-rile.mkv");
        var sample = Path.Combine(dir, "neustart.s03e01.german.dl.1080p.web.h265-rile-sample.mkv");
        File.Copy(m.Long720, original);
        File.Copy(m.Sdr8, sample);
        var p = FilmKonverter.Core.Settings.Profile.Nvidia();
        p.InputDir = Path.Combine(root, "Serien");
        p.OutputDir = Path.Combine(root, "Komprimiert");
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        Assert.Contains("sample", p.ExcludePatterns);

        var a = await LibraryAnalyzer.AnalyzeAsync(p, m.Tools, new FilmKonverter.Core.Media.ProbeCache(), null, CancellationToken.None);
        Assert.DoesNotContain(a.Files, f => f.Path == sample);
        var set = Assert.Single(a.Duplicates);
        Assert.Contains(set.Files, f => f.Path == sample);
        Assert.Contains(set.Files, f => f.Path == original);
    }
}
