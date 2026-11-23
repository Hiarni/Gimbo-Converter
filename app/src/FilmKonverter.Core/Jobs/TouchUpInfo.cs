using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public static class TouchUpInfo
{
    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\', '/'), Path.GetFullPath(b.Trim()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool IsProfileTarget(string? folder, Profile? profile) =>
        string.IsNullOrWhiteSpace(folder) || (profile is not null && Library.Scanner.IsInside(folder.Trim(), profile.OutputDir));

    public static List<string> RemovalRules(Profile p)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.AudioLanguages))
            list.Add(L.T($"Tonspuren in anderen Sprachen als: {p.AudioLanguages.Trim()} (Spuren ohne Sprachangabe bleiben)",
                         $"audio tracks in languages other than: {p.AudioLanguages.Trim()} (tracks without a language stay)"));
        if (p.RemoveCommentary) list.Add(L.T("Kommentar-Tonspuren", "commentary audio tracks"));
        if (p.RemoveAudioDescription) list.Add(L.T("Hörfilm-Tonspuren (Audiodeskription)", "audio description tracks"));
        if (!string.IsNullOrWhiteSpace(p.SubtitleLanguages))
            list.Add(L.T($"Untertitel in anderen Sprachen als: {p.SubtitleLanguages.Trim()} (Untertitel ohne Sprachangabe bleiben)",
                         $"subtitles in languages other than: {p.SubtitleLanguages.Trim()} (subtitles without a language stay)"));
        if (p.RemoveImageSubtitles) list.Add(L.T("Bild-Untertitel (PGS, VobSub), erzwungene ausgenommen", "image subtitles (PGS, VobSub), except forced ones"));
        if (!p.KeepAttachments) list.Add(L.T("Anhänge (z.B. Schriftarten für Untertitel)", "attachments (e.g. fonts for subtitles)"));
        return list;
    }

    public static string ConfirmText(Profile p, string? folder, bool applyRules)
    {
        var own = IsProfileTarget(folder, p);
        var where = string.IsNullOrWhiteSpace(folder)
            ? L.T($"im Zielordner des Profils „{p.Name}“ ({p.OutputDir})", $"in the target folder of the profile “{p.Name}” ({p.OutputDir})")
            : L.T($"in {folder.Trim()}", $"in {folder.Trim()}");
        var safe = L.T("Das Video wird nur kopiert (nicht neu kodiert). Jede neue Datei wird geprüft, bevor sie die alte ersetzt.",
                       "The video is only copied (not re-encoded). Each new file is checked before it replaces the old one.");
        var only = L.T("Angefasst werden nur MKV-Dateien, bei denen sich wirklich etwas ändert. Sie werden an Ort und Stelle ersetzt.",
                       "Only MKV files that really change are touched. They are replaced in place.");
        var ask = L.T("Fortfahren?", "Continue?");
        if (!applyRules)
        {
            return L.T($"Die MKV-Dateien {where} werden nur für die Abspielbarkeit korrigiert: ein zu hoher Video-Level und Ton, den Fernseher nicht abspielen können. Es entfällt keine Spur. Die Einstellungen für den Ton kommen aus dem Profil „{p.Name}“.",
                       $"The MKV files {where} are only corrected for playability: a video level that is too high and audio that TVs cannot play. No track is dropped. The audio settings come from the profile “{p.Name}”.")
                   + "\n\n" + only + " " + safe + "\n\n" + ask;
        }
        var rules = RemovalRules(p);
        var removed = rules.Count == 0
            ? L.T("Nach diesem Profil wird keine Spur entfernt.", "This profile removes no tracks.")
            : L.T("Dabei werden entfernt:", "This removes:") + "\n" + string.Join("\n", rules.Select(r => "– " + r)) + "\n\n"
              + (own
                  ? L.T("Entfernte Spuren sind danach aus diesen Dateien weg. Zurückholen lassen sie sich nur, indem die Quelle noch einmal komprimiert wird.",
                        "Removed tracks are then gone from these files. They can only be brought back by compressing the source again.")
                  : L.T("Entfernte Spuren sind danach aus diesen Dateien weg und lassen sich nicht zurückholen: es wird keine Kopie aufbewahrt.",
                        "Removed tracks are then gone from these files and cannot be brought back: no copy is kept."));
        return L.T($"Die MKV-Dateien {where} werden nach den Ton- und Untertitel-Regeln des Profils „{p.Name}“ umgeschrieben. Außerdem werden ein zu hoher Video-Level und Ton, den Fernseher nicht abspielen können, korrigiert.",
                   $"The MKV files {where} are rewritten according to the audio and subtitle rules of the profile “{p.Name}”. A video level that is too high and audio that TVs cannot play are corrected as well.")
               + "\n\n" + removed + "\n\n" + only + " " + safe + "\n\n" + ask;
    }
}
