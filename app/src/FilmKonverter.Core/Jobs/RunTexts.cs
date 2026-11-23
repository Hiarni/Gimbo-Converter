using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public enum RunKind
{
    Library,
    Queue,
    Verify,
    TouchUp,
    TouchUpDryRun,
    Test,
}

public static class RunTexts
{
    private static string N(int n) => n.ToString("N0", Fmt.De);

    public static string Count(int n, string deOne, string deMany, string enOne, string enMany) =>
        $"{N(n)} {(n == 1 ? L.T(deOne, enOne) : L.T(deMany, enMany))}";

    public static string Files(int n) => Count(n, "Datei", "Dateien", "file", "files");

    public static string Errors(int n) => Count(n, "Fehler", "Fehler", "error", "errors");

    public static string Entries(int n) => Count(n, "Eintrag", "Einträge", "entry", "entries");

    public static string Eta(double seconds) =>
        seconds < 0 || double.IsNaN(seconds) ? L.T("noch --:--:--", "--:--:-- left") : L.T("noch ", "") + Fmt.Time(seconds) + L.T("", " left");

    public static string Speed(double factor) => factor > 0 ? factor.ToString("N2", Fmt.De) + "×" : "";

    public static string Gain(long bytes) => bytes > 0 ? Fmt.Size(bytes) : "0 MB";

    public static string SummaryLine(RunKind kind, RunTally t, RunCounters? last)
    {
        var other = t.Other > 0 ? L.T($" · {N(t.Other)} auf anderen PCs in Arbeit", $" · {N(t.Other)} in progress on other PCs") : "";
        switch (kind)
        {
            case RunKind.Verify:
                return L.T($"{Files(t.New)} geprüft und in Ordnung · {N(t.Ok)} schon früher geprüft · {Errors(t.Errors)}",
                           $"{Files(t.New)} verified without errors · {N(t.Ok)} verified earlier · {Errors(t.Errors)}") + other;
            case RunKind.TouchUp:
                return L.T($"{Files(t.New)} nachgebessert · {N(t.Ok)} ohne Änderung · {N(t.Skipped)} übersprungen · {Errors(t.Errors)}",
                           $"{Files(t.New)} touched up · {N(t.Ok)} unchanged · {N(t.Skipped)} skipped · {Errors(t.Errors)}") + other;
            case RunKind.TouchUpDryRun:
                var would = last?.WouldChange ?? 0;
                return L.T("Probelauf: ", "Dry run: ")
                       + (would == 1 ? L.T("1 Datei würde geändert", "1 file would change") : L.T($"{N(would)} Dateien würden geändert", $"{N(would)} files would change"))
                       + L.T($" · {N(t.Ok)} ohne Änderung · {Errors(t.Errors)}", $" · {N(t.Ok)} unchanged · {Errors(t.Errors)}") + other;
            case RunKind.Test:
                return t.Errors > 0 ? L.T("Test fehlgeschlagen, die Meldung steht im Verlauf.", "Test failed, the message is in the history.")
                    : t.Skipped > 0 ? L.T("Nichts zu testen, der Grund steht im Verlauf.", "Nothing to test, the reason is in the history.")
                    : L.T("Ergebnis auf der Seite „Werkzeuge“.", "Result on the “Tools” page.");
            default:
                return L.T($"{N(t.New)} komprimiert · {N(t.Ok)} bereits vorhanden · {N(t.Skipped)} übersprungen · {Errors(t.Errors)}",
                           $"{N(t.New)} compressed · {N(t.Ok)} already done · {N(t.Skipped)} skipped · {Errors(t.Errors)}") + other;
        }
    }

    public static string Summary(RunKind kind, RunTally t, RunCounters? last)
    {
        var line = SummaryLine(kind, t, last);
        var gains = kind is RunKind.Library or RunKind.Queue or RunKind.TouchUp && t.GainBytes > 0;
        return gains ? line + "\n" + L.T($"{Gain(t.GainBytes)} gewonnen", $"{Gain(t.GainBytes)} saved") : line;
    }

    public static string SinceStart(RunTally t) => t.Pass <= 1 ? "" :
        L.T($"Seit dem Start: {Files(t.TotalNew)} komprimiert, {Gain(t.TotalGainBytes)} gewonnen.",
            $"Since the start: {Files(t.TotalNew)} compressed, {Gain(t.TotalGainBytes)} saved.");

    public static string PassNote(RunTally t) => t.Pass <= 1 ? "" :
        SinceStart(t) + L.T($" Die Zähler zeigen Durchlauf {N(t.Pass)}.", $" The counters show pass {N(t.Pass)}.");

    public static string FinishHint(RunKind kind, bool cancelled, RunTally t, RunCounters? last, int queueLeft = 0)
    {
        var total = last?.Total ?? 0;
        var filtered = last?.Filtered ?? 0;
        var nothing = !cancelled && t.Done == 0 && total == 0;
        switch (kind)
        {
            case RunKind.Library:
                if (nothing && filtered == 0)
                    return L.T(" Im Quellordner liegt keine passende Videodatei (Profil → Auswahl → Dateitypen prüfen).",
                               " There is no matching video file in the source folder (check profile → Selection → File types).");
                if (filtered == 0) return "";
                var text = L.T($" Ausgelassen: {last!.FilterText}.", $" Left out: {last!.FilterText}.");
                if (last.TooNew > 0) text += TooNewHint;
                return text;
            case RunKind.Queue:
                if (cancelled) return "";
                if (t.Done == 0)
                    return queueLeft > 0
                        ? L.T(" Noch nichts bearbeitet: Was in der Warteschlange steht, ist noch nicht dran (erst vor Kurzem geändert, siehe Profil → Auswahl → Neue Dateien abwarten, oder gerade nicht erreichbar). Die Einträge bleiben stehen und kommen beim nächsten Lauf dran.",
                              " Nothing processed yet: what is in the queue is not due yet (changed only recently, see profile → Selection → Wait for new files, or not reachable right now). The entries stay and are processed in the next run.")
                        : L.T(" In der Warteschlange war nichts zu bearbeiten (keine passende Videodatei in der Auswahl).",
                              " There was nothing to process in the queue (no matching video file in the selection).");
                return queueLeft > 0
                    ? L.T($" {Entries(queueLeft)} in der Warteschlange noch offen (zu neu, auf einem anderen PC in Arbeit oder gerade nicht erreichbar): beim nächsten Lauf.",
                          $" {Entries(queueLeft)} still open in the queue (too new, in progress on another PC or not reachable right now): in the next run.")
                    : "";
            case RunKind.Verify:
                return nothing
                    ? L.T(" Im Zielordner liegt keine fertige Datei zum Prüfen (geprüft werden MKV-Dateien; mit „Nur Dateien der letzten Tage“ nur die jüngeren).",
                          " There is no finished file to verify in the target folder (MKV files are checked; with “Only check files from the last days” only the recent ones).")
                    : "";
            case RunKind.TouchUp:
            case RunKind.TouchUpDryRun:
                return nothing
                    ? L.T(" In diesem Ordner liegt keine MKV-Datei. Nachgebessert werden nur MKV-Dateien.",
                          " There is no MKV file in this folder. Only MKV files are touched up.")
                    : "";
            default:
                return "";
        }
    }

    private static string TooNewHint =>
        L.T(" „Zu neu“ = erst vor Kurzem geändert oder noch nicht fertig kopiert. Diese Dateien kommen dran, sobald die Wartezeit um ist (Profil → Auswahl → Neue Dateien abwarten).",
            " “Too new” = changed only recently or not completely copied yet. These files are processed once the waiting time has passed (profile → Selection → Wait for new files).");

    public static string FinishStatus(RunKind kind, bool cancelled, RunTally t, RunCounters? last, int queueLeft = 0)
    {
        var text = cancelled ? L.T("Angehalten.", "Stopped.") : L.T("Fertig.", "Done.");
        switch (kind)
        {
            case RunKind.Verify or RunKind.TouchUp or RunKind.TouchUpDryRun when t.Done > 0:
                text += " " + SummaryLine(kind, t, last) + ".";
                break;
            case RunKind.Test when !cancelled:
                text += " " + SummaryLine(kind, t, last);
                break;
        }
        return text + FinishHint(kind, cancelled, t, last, queueLeft);
    }
}
