using System.Collections.Concurrent;
using System.Text.Json;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Rename;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

public sealed class RunControlTests
{
    private static void WaitUntil(Func<bool> done, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(done(), "Zeit abgelaufen");
    }

    // ---------------- BusyFlag ----------------

    [Fact]
    public void BusyFlag_holt_den_zuletzt_uebergebenen_Auftrag_nach()
    {
        var flag = new BusyFlag();
        var log = new ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        flag.Run(() => { log.Enqueue("A"); started.Set(); gate.Wait(5000); });
        Assert.True(started.Wait(5000));
        flag.Run(() => log.Enqueue("B"));
        flag.Run(() => log.Enqueue("C"));
        gate.Set();
        WaitUntil(() => log.Count >= 2 && !flag.IsBusy);
        Thread.Sleep(50);
        Assert.Equal(new[] { "A", "C" }, log.ToArray());
    }

    [Fact]
    public void BusyFlag_verliert_den_letzten_Auftrag_nicht_und_bleibt_nicht_haengen()
    {
        for (var round = 0; round < 50; round++)
        {
            var flag = new BusyFlag();
            var executed = 0;
            for (var i = 1; i <= 200; i++)
            {
                var n = i;
                flag.Run(() => Volatile.Write(ref executed, n));
            }
            WaitUntil(() => Volatile.Read(ref executed) == 200);
            WaitUntil(() => !flag.IsBusy);
            Assert.True(flag.TryEnter());
        }
    }

    [Fact]
    public void BusyFlag_uebersteht_einen_Auftrag_der_scheitert()
    {
        var flag = new BusyFlag();
        var ran = false;
        flag.Run(() => throw new IOException("Netzlaufwerk weg"));
        WaitUntil(() => !flag.IsBusy);
        flag.Run(() => ran = true);
        WaitUntil(() => ran);
    }

    // ---------------- Automatische Pause ----------------

    private static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0);

    // ---------------- Zaehler je Durchlauf ----------------

    [Fact]
    public void Von_Hand_fortgesetzt_wird_nicht_nach_fuenf_Sekunden_wieder_pausiert()
    {
        var s = new AutoPauseState();
        Assert.Equal(AutoPauseAction.Pause, s.Decide(enabled: true, running: true, paused: false, busy: true, T0));
        s.MarkPaused();
        Assert.True(s.AutoPaused);

        s.UserToggled();
        Assert.False(s.AutoPaused);
        Assert.True(s.ManualOverride);
        for (var i = 1; i <= 100; i++)
            Assert.Equal(AutoPauseAction.None, s.Decide(true, true, paused: false, busy: true, T0.AddSeconds(5 * i)));

        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, false, busy: false, T0.AddMinutes(10)));
        Assert.False(s.ManualOverride);
        Assert.Equal(AutoPauseAction.Pause, s.Decide(true, true, false, busy: true, T0.AddMinutes(11)));
    }

    [Fact]
    public void Automatische_Pause_endet_erst_nach_dreissig_freien_Sekunden()
    {
        var s = new AutoPauseState();
        Assert.Equal(AutoPauseAction.Pause, s.Decide(true, true, false, true, T0));
        s.MarkPaused();
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, true, T0.AddSeconds(5)));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, false, T0.AddSeconds(10)));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, false, T0.AddSeconds(35)));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, true, T0.AddSeconds(38)));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, false, T0.AddSeconds(40)));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, true, false, T0.AddSeconds(65)));
        Assert.Equal(AutoPauseAction.Resume, s.Decide(true, true, true, false, T0.AddSeconds(70)));
        s.MarkResumed();
        Assert.False(s.AutoPaused);
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, false, false, T0.AddSeconds(75)));
    }

    [Fact]
    public void Abgeschaltete_Pause_Einstellungen_heben_die_automatische_Pause_sofort_auf()
    {
        var s = new AutoPauseState();
        Assert.Equal(AutoPauseAction.Pause, s.Decide(true, true, false, true, T0));
        s.MarkPaused();
        Assert.Equal(AutoPauseAction.Resume, s.Decide(enabled: false, running: true, paused: true, busy: false, T0.AddSeconds(5)));
        s.MarkResumed();
        Assert.Equal(AutoPauseAction.None, s.Decide(false, true, false, false, T0.AddSeconds(10)));
    }

    [Fact]
    public void Von_Hand_pausiert_bleibt_pausiert()
    {
        var s = new AutoPauseState();
        s.UserToggled();
        Assert.False(s.ManualOverride);
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, paused: true, busy: true, T0));
        Assert.Equal(AutoPauseAction.None, s.Decide(true, true, paused: true, busy: false, T0.AddMinutes(5)));
        Assert.Equal(AutoPauseAction.None, s.Decide(false, true, paused: true, busy: false, T0.AddMinutes(6)));
    }

    [Fact]
    public void Pause_die_nicht_geklappt_hat_wird_beim_naechsten_Mal_erneut_versucht()
    {
        var s = new AutoPauseState();
        Assert.Equal(AutoPauseAction.Pause, s.Decide(true, true, false, true, T0));
        Assert.Equal(AutoPauseAction.Pause, s.Decide(true, true, false, true, T0.AddSeconds(5)));
        Assert.False(s.AutoPaused);
    }

    [Fact]
    public void Ohne_laufende_Arbeit_bleibt_nichts_haengen()
    {
        var s = new AutoPauseState();
        s.Decide(true, true, false, true, T0);
        s.MarkPaused();
        s.UserToggled();
        Assert.Equal(AutoPauseAction.None, s.Decide(true, running: false, false, true, T0.AddSeconds(5)));
        Assert.False(s.AutoPaused);
        Assert.False(s.ManualOverride);
    }

    [Fact]
    public void Zaehler_gelten_je_Durchlauf_die_Summe_seit_dem_Start_bleibt()
    {
        const long gb = 1024L * 1024 * 1024;
        var t = new RunTally();
        t.Count(new FileEnd(Outcome.Encoded, "", 2 * gb));
        t.Count(new FileEnd(Outcome.Replaced, "", gb));
        t.Count(new FileEnd(Outcome.AlreadyOk, ""));
        t.Count(new FileEnd(Outcome.Error, ""));
        Assert.Equal((4, 2, 1, 1), (t.Done, t.New, t.Ok, t.Errors));
        Assert.Equal("", RunTexts.SinceStart(t));

        t.NextPass();
        Assert.Equal((0, 0, 0, 0, 0L), (t.Done, t.New, t.Ok, t.Errors, t.GainBytes));
        t.Count(new FileEnd(Outcome.AlreadyOk, ""));
        t.Count(new FileEnd(Outcome.AlreadyOk, ""));
        t.Count(new FileEnd(Outcome.AlreadyOk, ""));
        t.Count(new FileEnd(Outcome.Error, ""));
        t.Count(new FileEnd(Outcome.Encoded, "", gb));
        Assert.Equal((5, 1, 3, 1), (t.Done, t.New, t.Ok, t.Errors));
        Assert.Equal((3, 4 * gb, 2), (t.TotalNew, t.TotalGainBytes, t.Pass));
        Assert.Equal("Seit dem Start: 3 Dateien komprimiert, 4,00 GB gewonnen.", RunTexts.SinceStart(t));
        Assert.EndsWith("Die Zähler zeigen Durchlauf 2.", RunTexts.PassNote(t));
    }

    [Fact]
    public void Verlauf_wiederholt_nicht_in_jedem_Durchlauf_dasselbe()
    {
        var t = new RunTally();
        var hevc = new FileEnd(Outcome.Skipped, "Quelle ist bereits HEVC.");
        Assert.True(t.ShowInHistory("a.mkv", new FileEnd(Outcome.AlreadyOk, "")));
        Assert.True(t.ShowInHistory("b.mkv", hevc));
        Assert.True(t.ShowInHistory("c.mkv", new FileEnd(Outcome.OtherPc, "bei PC-2 in Arbeit")));
        Assert.True(t.ShowInHistory("d.mkv", new FileEnd(Outcome.Error, "Quelle nicht lesbar (ffprobe).")));

        t.NextPass();
        Assert.False(t.ShowInHistory("a.mkv", new FileEnd(Outcome.AlreadyOk, "")));
        Assert.False(t.ShowInHistory("neu.mkv", new FileEnd(Outcome.AlreadyOk, "")));
        Assert.False(t.ShowInHistory("b.mkv", hevc));
        Assert.False(t.ShowInHistory("B.MKV", hevc));
        Assert.False(t.ShowInHistory("c.mkv", new FileEnd(Outcome.OtherPc, "bei PC-2 in Arbeit")));
        Assert.True(t.ShowInHistory("e.mkv", hevc));
        Assert.True(t.ShowInHistory("b.mkv", new FileEnd(Outcome.Skipped, "Ausnahmeliste: schon versucht, nicht klein genug.")));
        Assert.True(t.ShowInHistory("c.mkv", new FileEnd(Outcome.OtherPc, "bei PC-1 in Arbeit")));
        Assert.True(t.ShowInHistory("d.mkv", new FileEnd(Outcome.Error, "Quelle nicht lesbar (ffprobe).")));
        foreach (var o in new[] { Outcome.Encoded, Outcome.Replaced, Outcome.Cancelled })
            Assert.True(t.ShowInHistory("b.mkv", new FileEnd(o, "")));

        t.NextPass();
        Assert.False(t.ShowInHistory("e.mkv", hevc));

        t.Reset();
        Assert.True(t.ShowInHistory("a.mkv", new FileEnd(Outcome.AlreadyOk, "")));
        Assert.True(t.ShowInHistory("b.mkv", hevc));
    }

    // ---------------- Texte ----------------

    private static RunTally Tally(params Outcome[] outcomes)
    {
        var t = new RunTally();
        foreach (var o in outcomes) t.Count(new FileEnd(o, ""));
        return t;
    }

    [Fact]
    public void Einzahl_und_Mehrzahl()
    {
        Assert.Equal("1 Datei", RunTexts.Files(1));
        Assert.Equal("0 Dateien", RunTexts.Files(0));
        Assert.Equal("12 Dateien", RunTexts.Files(12));
        Assert.Equal("1 Eintrag", RunTexts.Entries(1));
        Assert.Equal("3 Einträge", RunTexts.Entries(3));
        Assert.Equal("1 Fehler", RunTexts.Errors(1));
        Assert.StartsWith("1 ", RunTexts.Files(1));
        Assert.Contains("1 Datei geprüft und in Ordnung", RunTexts.SummaryLine(RunKind.Verify, Tally(Outcome.Encoded), null));
        Assert.Contains("2 Dateien geprüft und in Ordnung", RunTexts.SummaryLine(RunKind.Verify, Tally(Outcome.Encoded, Outcome.Encoded), null));
    }

    [Fact]
    public void Restzeit_und_Tempo_haben_eine_Schreibweise()
    {
        Assert.Equal("noch 00:07:37", RunTexts.Eta(457));
        Assert.Equal("noch --:--:--", RunTexts.Eta(-1));
        Assert.Equal("3,89×", RunTexts.Speed(3.89));
        Assert.Equal("4,20×", RunTexts.Speed(4.2));
        Assert.Equal("", RunTexts.Speed(0));
        Assert.Equal("0 MB", RunTexts.Gain(0));
    }

    [Fact]
    public void Zusammenfassung_spricht_die_Sprache_der_Lauf_Art()
    {
        var t = Tally(Outcome.Replaced, Outcome.Replaced, Outcome.AlreadyOk, Outcome.Error);
        Assert.Equal("2 komprimiert · 1 bereits vorhanden · 0 übersprungen · 1 Fehler", RunTexts.SummaryLine(RunKind.Library, t, null));
        Assert.Equal("2 Dateien nachgebessert · 1 ohne Änderung · 0 übersprungen · 1 Fehler", RunTexts.SummaryLine(RunKind.TouchUp, t, null));
        Assert.DoesNotContain("komprimiert", RunTexts.Summary(RunKind.TouchUp, t, null));
        Assert.DoesNotContain("komprimiert", RunTexts.Summary(RunKind.Verify, Tally(Outcome.Encoded, Outcome.AlreadyOk), null));
        Assert.Equal("Fertig. 1 Datei geprüft und in Ordnung · 1 schon früher geprüft · 0 Fehler.",
            RunTexts.FinishStatus(RunKind.Verify, false, Tally(Outcome.Encoded, Outcome.AlreadyOk), new RunCounters { Total = 2 }));
        Assert.EndsWith("· 1 auf anderen PCs in Arbeit", RunTexts.SummaryLine(RunKind.Library, Tally(Outcome.OtherPc), null));
    }

    [Fact]
    public void Probelauf_nennt_wie_viele_Dateien_geaendert_wuerden()
    {
        var t = Tally(Outcome.Skipped, Outcome.Skipped, Outcome.AlreadyOk);
        Assert.Equal("Probelauf: 12 Dateien würden geändert · 1 ohne Änderung · 0 Fehler",
            RunTexts.SummaryLine(RunKind.TouchUpDryRun, t, new RunCounters { Total = 13, WouldChange = 12 }));
        Assert.StartsWith("Probelauf: 1 Datei würde geändert", RunTexts.SummaryLine(RunKind.TouchUpDryRun, t, new RunCounters { WouldChange = 1 }));
        Assert.StartsWith("Fertig. Probelauf: 12 Dateien würden geändert", RunTexts.FinishStatus(RunKind.TouchUpDryRun, false, t, new RunCounters { Total = 13, WouldChange = 12 }));
    }

    [Fact]
    public void Gewonnener_Platz_nur_wo_es_ihn_gibt()
    {
        var t = new RunTally();
        t.Count(new FileEnd(Outcome.Encoded, "", 3L * 1024 * 1024 * 1024));
        Assert.EndsWith("\n3,00 GB gewonnen", RunTexts.Summary(RunKind.Library, t, null));
        Assert.DoesNotContain("gewonnen", RunTexts.Summary(RunKind.Verify, t, null));
        Assert.DoesNotContain("gewonnen", RunTexts.Summary(RunKind.Library, Tally(Outcome.AlreadyOk), null));
    }

    [Fact]
    public void Hinweis_Bibliothek_nennt_die_Einstellung_bei_ihrem_Namen()
    {
        var none = RunTexts.FinishHint(RunKind.Library, false, new RunTally(), new RunCounters());
        Assert.Contains("keine passende Videodatei", none);
        Assert.Contains("Dateitypen", none);

        var c = new RunCounters { Total = 3, Filtered = 2, FilterText = "2 zu neu", TooNew = 2 };
        var hint = RunTexts.FinishHint(RunKind.Library, false, Tally(Outcome.AlreadyOk), c);
        Assert.StartsWith(" Ausgelassen: 2 zu neu.", hint);
        Assert.Contains("Neue Dateien abwarten", hint);
        Assert.DoesNotContain("Zu neue Dateien auslassen", hint);
        Assert.Contains("erst vor Kurzem geändert", hint);

        var small = RunTexts.FinishHint(RunKind.Library, false, Tally(Outcome.AlreadyOk), new RunCounters { Total = 1, Filtered = 1, FilterText = "1 zu klein" });
        Assert.Equal(" Ausgelassen: 1 zu klein.", small);
        Assert.Equal("", RunTexts.FinishHint(RunKind.Library, false, Tally(Outcome.Encoded), new RunCounters { Total = 1 }));
    }

    [Fact]
    public void Hinweis_passt_zur_Lauf_Art()
    {
        var empty = new RunCounters();
        var verify = RunTexts.FinishHint(RunKind.Verify, false, new RunTally(), empty);
        Assert.Contains("Zielordner", verify);
        Assert.DoesNotContain("Quellordner", verify);
        foreach (var kind in new[] { RunKind.TouchUp, RunKind.TouchUpDryRun })
        {
            var touch = RunTexts.FinishHint(kind, false, new RunTally(), empty);
            Assert.Contains("keine MKV-Datei", touch);
            Assert.DoesNotContain("Quellordner", touch);
        }
        Assert.Equal("", RunTexts.FinishHint(RunKind.Test, false, new RunTally(), empty));
        Assert.Equal("", RunTexts.FinishHint(RunKind.Verify, false, Tally(Outcome.Encoded), new RunCounters { Total = 1 }));
    }

    [Fact]
    public void Hinweis_Warteschlange()
    {
        var empty = new RunCounters();
        var waiting = RunTexts.FinishHint(RunKind.Queue, false, new RunTally(), empty, queueLeft: 1);
        Assert.Contains("Neue Dateien abwarten", waiting);
        Assert.Contains("bleiben stehen", waiting);
        Assert.DoesNotContain("Dateitypen", waiting);
        Assert.Contains("nichts zu bearbeiten", RunTexts.FinishHint(RunKind.Queue, false, new RunTally(), empty, queueLeft: 0));
        Assert.StartsWith(" 2 Einträge in der Warteschlange noch offen", RunTexts.FinishHint(RunKind.Queue, false, Tally(Outcome.Encoded), empty, queueLeft: 2));
        Assert.StartsWith(" 1 Eintrag in der Warteschlange noch offen", RunTexts.FinishHint(RunKind.Queue, false, Tally(Outcome.Encoded), empty, queueLeft: 1));
        Assert.Equal("", RunTexts.FinishHint(RunKind.Queue, false, Tally(Outcome.Encoded), empty, queueLeft: 0));
    }

    [Fact]
    public void Abgebrochen_behauptet_nicht_es_gebe_nichts()
    {
        foreach (var kind in Enum.GetValues<RunKind>())
        {
            Assert.Equal("", RunTexts.FinishHint(kind, cancelled: true, new RunTally(), new RunCounters(), queueLeft: 3));
            Assert.Equal("Angehalten.", RunTexts.FinishStatus(kind, cancelled: true, new RunTally(), new RunCounters(), queueLeft: 3));
        }
        Assert.Equal("Fertig.", RunTexts.FinishStatus(RunKind.Library, false, Tally(Outcome.Encoded), new RunCounters { Total = 1 }));
    }

    [Fact]
    public void Testmodus_sagt_wo_das_Ergebnis_steht()
    {
        Assert.Equal("Fertig. Ergebnis auf der Seite „Werkzeuge“.", RunTexts.FinishStatus(RunKind.Test, false, Tally(Outcome.Encoded), null));
        Assert.Contains("Nichts zu testen", RunTexts.FinishStatus(RunKind.Test, false, Tally(Outcome.Skipped), null));
        Assert.Contains("fehlgeschlagen", RunTexts.FinishStatus(RunKind.Test, false, Tally(Outcome.Error), null));
        Assert.DoesNotContain("komprimiert", RunTexts.Summary(RunKind.Test, Tally(Outcome.Encoded), null));
    }

    // ---------------- Kleine Entscheidungen ----------------

    [Theory]
    [InlineData(1, 60, 1)]
    [InlineData(2, 60, 2)]
    [InlineData(3, 60, 5)]
    [InlineData(50, 60, 5)]
    [InlineData(3, 2, 2)]
    [InlineData(1, 0, 1)]
    public void Dauerbetrieb_versucht_es_nach_einem_Fehlschlag_bald_wieder(int failures, int rescan, int expected) =>
        Assert.Equal(expected, RunPolicy.RetryMinutes(failures, rescan));

    // ---------------- Nachbessern: Rueckfrage ----------------

    [Fact]
    public void Standby_wird_nur_verhindert_wenn_gearbeitet_wird()
    {
        Assert.False(RunPolicy.NeedsAwake(running: false, fileInProgress: false, windowClosed: false, waitingIdle: false));
        Assert.True(RunPolicy.NeedsAwake(true, fileInProgress: true, windowClosed: false, waitingIdle: false));
        Assert.True(RunPolicy.NeedsAwake(true, fileInProgress: false, windowClosed: false, waitingIdle: false));
        Assert.False(RunPolicy.NeedsAwake(true, fileInProgress: false, windowClosed: true, waitingIdle: false));
        Assert.False(RunPolicy.NeedsAwake(true, fileInProgress: false, windowClosed: false, waitingIdle: true));
        Assert.True(RunPolicy.NeedsAwake(true, fileInProgress: true, windowClosed: true, waitingIdle: true));
    }

    // ---------------- Umbenennen waehrend eines Laufs ----------------

    [Fact]
    public void Nachbessern_nennt_das_Profil_und_was_entfernt_wird()
    {
        var p = Profile.Nvidia();
        p.Name = "Wohnzimmer";
        p.OutputDir = Path.Combine(Path.GetTempPath(), "ziel");
        p.AudioLanguages = "de, en";
        p.RemoveCommentary = true;
        p.RemoveAudioDescription = true;
        p.SubtitleLanguages = "de";
        p.RemoveImageSubtitles = true;
        p.KeepAttachments = false;
        Assert.Equal(6, TouchUpInfo.RemovalRules(p).Count);

        var library = Path.Combine(Path.GetTempPath(), "Mediathek");
        var text = TouchUpInfo.ConfirmText(p, library, applyRules: true);
        Assert.Contains("„Wohnzimmer“", text);
        Assert.Contains(library, text);
        Assert.Contains("– Tonspuren in anderen Sprachen als: de, en", text);
        Assert.Contains("– Kommentar-Tonspuren", text);
        Assert.Contains("– Hörfilm-Tonspuren", text);
        Assert.Contains("– Untertitel in anderen Sprachen als: de", text);
        Assert.Contains("– Bild-Untertitel", text);
        Assert.Contains("– Anhänge", text);
        Assert.Contains("nur MKV-Dateien", text);
        Assert.Contains("an Ort und Stelle ersetzt", text);
        Assert.DoesNotContain("Quelle", text);
        Assert.Contains("lassen sich nicht zurückholen", text);

        var own = TouchUpInfo.ConfirmText(p, "", applyRules: true);
        Assert.Contains("im Zielordner des Profils „Wohnzimmer“", own);
        Assert.Contains("die Quelle noch einmal komprimiert", own);
    }

    [Fact]
    public void Nachbessern_ohne_Regeln_entfernt_nichts()
    {
        var p = Profile.Nvidia();
        p.AudioLanguages = "de";
        var text = TouchUpInfo.ConfirmText(p, Path.Combine(Path.GetTempPath(), "Mediathek"), applyRules: false);
        Assert.Contains("Es entfällt keine Spur", text);
        Assert.DoesNotContain("Tonspuren in anderen Sprachen", text);
        Assert.Contains($"„{p.Name}“", text);

        var plain = Profile.Nvidia();
        Assert.Empty(TouchUpInfo.RemovalRules(plain));
        Assert.Contains("Nach diesem Profil wird keine Spur entfernt.", TouchUpInfo.ConfirmText(plain, "", applyRules: true));
    }

    [Fact]
    public void Nachbessern_erkennt_den_Zielordner_des_Profils()
    {
        var p = Profile.Nvidia();
        p.OutputDir = Path.Combine(Path.GetTempPath(), "ziel");
        Assert.True(TouchUpInfo.IsProfileTarget("", p));
        Assert.True(TouchUpInfo.IsProfileTarget("  ", p));
        Assert.True(TouchUpInfo.IsProfileTarget(p.OutputDir + Path.DirectorySeparatorChar, p));
        Assert.False(TouchUpInfo.IsProfileTarget(Path.Combine(Path.GetTempPath(), "Mediathek"), p));
        Assert.True(TouchUpInfo.IsProfileTarget(Path.Combine(p.OutputDir, "Insel", "Staffel 1"), p));
        Assert.False(TouchUpInfo.IsProfileTarget(Path.Combine(Path.GetTempPath(), "Mediathek"), null));
    }

    [Fact]
    public void Umbenennen_ist_nur_gesperrt_wo_der_Lauf_arbeitet()
    {
        var root = Path.Combine(Path.GetTempPath(), "guard-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        var other = Path.Combine(root, "Downloads");
        var lib = new RenameLibrary(input, output);
        RenameItem Item(string file, RenameLibrary? l) => new() { Source = file, RelativeSource = Path.GetFileName(file), Info = new EpisodeInfo(), Library = l };

        var inLib = Item(Path.Combine(input, "Insel", "Staffel 1", "S01E01.mkv"), lib);
        var inLibNoTarget = Item(Path.Combine(input, "Insel", "Staffel 1", "S01E01.mkv"), null);
        var free = Item(Path.Combine(other, "Film.mkv"), null);

        string[] libraryRun = [input, output];
        Assert.NotNull(RenameGuard.Conflict(RenameGuard.Touched(inLib), libraryRun));
        Assert.NotNull(RenameGuard.Conflict(RenameGuard.Touched(inLibNoTarget), libraryRun));
        Assert.Null(RenameGuard.Conflict(RenameGuard.Touched(free), libraryRun));

        string[] verify = [output];
        Assert.Equal(Path.Combine(output, "Insel", "Staffel 1", "S01E01.mkv"), RenameGuard.Conflict(RenameGuard.Touched(inLib), verify));
        Assert.Null(RenameGuard.Conflict(RenameGuard.Touched(inLibNoTarget), verify));

        Assert.NotNull(RenameGuard.Conflict(RenameGuard.Touched(free), [other]));
        Assert.Null(RenameGuard.Conflict(RenameGuard.Touched(inLib), [other]));
        Assert.Null(RenameGuard.Conflict([Path.Combine(root, "Serien2", "x.mkv")], [input]));

        string[] test = [inLib.Source];
        Assert.NotNull(RenameGuard.Conflict(RenameGuard.Touched(inLib), test));
        Assert.Null(RenameGuard.Conflict([Path.Combine(input, "Insel", "Staffel 1", "S01E02.mkv")], test));

        Assert.Null(RenameGuard.Conflict(RenameGuard.Touched(inLib), []));
    }

    [Fact]
    public void Rueckgaengig_wird_nach_den_Dateien_des_Protokolls_beurteilt()
    {
        var root = Path.Combine(Path.GetTempPath(), "guard-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            var journal = Path.Combine(root, "2026-11-13_10-00-00_PC.jsonl");
            var from = Path.Combine(root, "Downloads", "alt.mkv");
            var to = Path.Combine(root, "Downloads", "neu.mkv");
            File.WriteAllLines(journal,
            [
                JsonSerializer.Serialize(new RenamePlanner.JournalHeader(Path.Combine(root, "Downloads"), "", "PC", DateTime.Now, 1)),
                JsonSerializer.Serialize(new FileMove(from, to)),
            ]);
            var files = RenameGuard.JournalFiles(journal)!;
            Assert.Equal(new List<string> { to, from }, files);
            Assert.Null(RenameGuard.Conflict(files, [Path.Combine(root, "Serien"), Path.Combine(root, "Komprimiert")]));
            Assert.NotNull(RenameGuard.Conflict(files, [Path.Combine(root, "Downloads")]));
            Assert.Null(RenameGuard.JournalFiles(Path.Combine(root, "fehlt.jsonl")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

[Collection("media")]
public sealed class RunControlMediaTests(TestMedia m)
{
    private sealed class Watch : IRunObserver
    {
        public RunTally Tally { get; } = new();
        public RunCounters? Last { get; private set; }
        public bool? Cancelled { get; private set; }
        public int Finishes { get; private set; }
        public List<FileStart> Starts { get; } = [];
        public void ScanStarted(string folder) { }
        public void ScanDone(int total) { }
        public void FileStarted(FileStart start) { lock (Starts) Starts.Add(start); }
        public void Line(InfoLine line) { }
        public void Progress(string stage, ProgressInfo info) { }
        public void FileFinished(FileStart start, FileEnd end) { lock (Tally) Tally.Count(end); }
        public void RunFinished(RunCounters counters, bool cancelled) { Last = counters; Cancelled = cancelled; Finishes++; }
    }

    private static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "runctl-" + Guid.NewGuid().ToString("N")[..6], name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Profile NewProfile(out string input, out string output)
    {
        var root = NewDir("lib");
        input = Path.Combine(root, "in");
        output = Path.Combine(root, "out");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var p = Profile.Cpu();
        p.InputDir = input;
        p.OutputDir = output;
        p.MinSizeMb = 0;
        return p;
    }

    [Fact]
    public async Task Nicht_erreichbare_Quelle_wirft_und_meldet_vorher_ein_Ende_ohne_Abbruch()
    {
        var p = NewProfile(out var input, out _);
        Directory.Delete(input);
        var w = new Watch();
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new LibraryRun(p, m.Tools, w) { Cache = null, UseQueue = true }.RunAsync(CancellationToken.None));
        Assert.Contains("Quellordner nicht erreichbar", ex.Message);
        Assert.Equal(1, w.Finishes);
        Assert.False(w.Cancelled);
        Assert.Equal(0, w.Tally.Done);
    }

    [Fact]
    public async Task Warteschlange_mit_gerade_geaenderter_Datei_bekommt_den_passenden_Hinweis()
    {
        var p = NewProfile(out var input, out var output);
        var series = Path.Combine(input, "Serie");
        Directory.CreateDirectory(series);
        File.Copy(m.Sdr8, Path.Combine(series, "S01E01.mkv"));
        Assert.Equal(1, WorkQueue.Add(output, input, [series], "PC1"));

        var w = new Watch();
        await new LibraryRun(p, m.Tools, w) { QueueOnly = true, Cache = null }.RunAsync(CancellationToken.None);
        var left = WorkQueue.Read(output).Count;
        Assert.Equal(0, w.Tally.Done);
        Assert.Equal(1, left);
        var status = RunTexts.FinishStatus(RunKind.Queue, w.Cancelled == true, w.Tally, w.Last, left);
        Assert.StartsWith("Fertig. Noch nichts bearbeitet", status);
        Assert.Contains("Neue Dateien abwarten", status);
        Assert.DoesNotContain("Keine passende Videodatei", status);
    }

    [Fact]
    public async Task Warteschlangen_Dateien_zaehlen_in_der_Gesamtzahl_mit()
    {
        var p = NewProfile(out var input, out var output);
        p.SkipNewerThanMinutes = 0;
        p.SkipHevc = true;
        var series = Path.Combine(input, "Serie");
        Directory.CreateDirectory(series);
        File.Copy(m.Hevc8, Path.Combine(series, "S01E01.mkv"));
        File.Copy(m.Hevc8, Path.Combine(series, "S01E02.mkv"));
        Assert.Equal(1, WorkQueue.Add(output, input, [series], "PC1"));

        var w = new Watch();
        await new LibraryRun(p, m.Tools, w) { QueueOnly = true, Cache = null, WriteStatsLog = false }.RunAsync(CancellationToken.None);
        Assert.Equal(2, w.Tally.Done);
        Assert.Equal(2, w.Starts.Max(s => s.Total));
        Assert.All(w.Starts, s => Assert.True(s.Total >= s.Number));
        Assert.Empty(WorkQueue.Read(output));
        Assert.Equal("Fertig.", RunTexts.FinishStatus(RunKind.Queue, false, w.Tally, w.Last, 0));
    }

    [Fact]
    public async Task Prueflauf_und_Nachbessern_ohne_Dateien_sagen_was_fehlt()
    {
        var p = NewProfile(out _, out var output);
        var w = new Watch();
        await new VerifyRun(p, m.Tools, w).RunAsync(new VerifyOptions(), CancellationToken.None);
        var verify = RunTexts.FinishStatus(RunKind.Verify, w.Cancelled == true, w.Tally, w.Last);
        Assert.StartsWith("Fertig. Im Zielordner liegt keine fertige Datei zum Prüfen", verify);

        var library = NewDir("Mediathek");
        File.Copy(m.Mp4Text, Path.Combine(library, "Film.mp4"));
        var w2 = new Watch();
        await new CleanupRun(p, m.Tools, w2) { Cache = null, WriteStatsLog = false }.RunAsync(new CleanupOptions { DryRun = true, Folder = library, ApplyProfileRules = false }, CancellationToken.None);
        var touch = RunTexts.FinishStatus(RunKind.TouchUpDryRun, w2.Cancelled == true, w2.Tally, w2.Last);
        Assert.StartsWith("Fertig. In diesem Ordner liegt keine MKV-Datei", touch);
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task Automatik_nimmt_die_neueste_Installation_die_den_Encoder_kennt()
    {
        static void SetVersion(FfmpegTools t, string v) => typeof(FfmpegTools).GetProperty(nameof(FfmpegTools.Version))!.SetValue(t, v);
        async Task<FfmpegTools> Load(string version, params string[] without)
        {
            var t = (await FfmpegTools.TryLoadAsync(m.Tools.Folder))!;
            SetVersion(t, version);
            foreach (var e in without) t.Encoders.Remove(e);
            foreach (var e in new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv" }) if (!without.Contains(e)) t.Encoders.Add(e);
            return t;
        }
        var bundled = new FfmpegCandidate(await Load("7.1"), true);
        var newerWithoutNvenc = new FfmpegCandidate(await Load("8.0.1", "hevc_nvenc"), false);
        var older = new FfmpegCandidate(await Load("6.1"), false);
        FfmpegCandidate[] all = [older, newerWithoutNvenc, bundled];

        Assert.Same(newerWithoutNvenc, FfmpegLocator.PickNewest(all));
        Assert.Same(bundled, FfmpegLocator.PickAuto(all, EncoderKind.Nvenc));
        Assert.Same(newerWithoutNvenc, FfmpegLocator.PickAuto(all, EncoderKind.Amf));
        Assert.Same(newerWithoutNvenc, FfmpegLocator.PickAuto(all, null));
        var noQsv = new FfmpegCandidate(await Load("9.0", "hevc_qsv"), false);
        var bundledNoQsv = new FfmpegCandidate(await Load("7.1", "hevc_qsv"), true);
        Assert.Same(bundledNoQsv, FfmpegLocator.PickAuto([noQsv, bundledNoQsv], EncoderKind.Qsv));
        Assert.Same(noQsv, FfmpegLocator.PickAuto([noQsv, new FfmpegCandidate(await Load("6.0", "hevc_qsv"), false)], EncoderKind.Qsv));
        Assert.Null(FfmpegLocator.PickAuto([], EncoderKind.Cpu));
        var twin = new FfmpegCandidate(await Load("7.1"), false);
        Assert.Same(bundled, FfmpegLocator.PickAuto([twin, bundled], EncoderKind.Nvenc));
    }
}
