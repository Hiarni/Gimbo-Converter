using System.Text.RegularExpressions;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Encoding;

public sealed record AudioTrackPlan(int OutputIndex, string SourceText, bool Convert, int OutChannels, int BitrateKbit)
{
    public int SourceIndex { get; init; }
    public int SourceChannels { get; init; }
    public bool Lossless { get; init; }
    public string Codec { get; init; } = "eac3";
    public string? Disposition { get; init; }
    public string SourceTitle { get; init; } = "";
    public string? NewTitle { get; init; }

    public string Text
    {
        get
        {
            var t = Convert
                ? Codec == "flac" ? $"{SourceText} → FLAC"
                : $"{SourceText} → {(Codec == "ac3" ? "AC3" : "E-AC3")} {AudioPlanner.ChannelText(OutChannels)} {BitrateKbit}k"
                : L.T($"{SourceText} kopiert", $"{SourceText} copied");
            return Disposition is not null && Disposition.Contains("default") ? t + L.T(" (Standard)", " (default)") : t;
        }
    }
}

public sealed record AudioPlan(IReadOnlyList<AudioTrackPlan> Tracks)
{
    public IReadOnlyList<string> Removed { get; init; } = [];

    public bool AllMapped { get; init; } = true;

    public bool MovedFirst { get; init; }

    public string Note { get; init; } = "";

    public int Converted => Tracks.Count(t => t.Convert && t.Codec != "flac");
    public int LosslessConverted => Tracks.Count(t => t.Convert && t.Lossless);
    public bool Changed => Tracks.Any(t => t.Convert) || Removed.Count > 0 || Tracks.Any(t => t.Disposition is not null) || MovedFirst || Note.Length > 0;

    public IEnumerable<string> TitleChanges => Tracks.Where(t => t.NewTitle is not null).Select(t => t.NewTitle!.Length == 0
        ? L.T($"„{t.SourceTitle}“ entfernt", $"“{t.SourceTitle}” removed")
        : $"„{t.SourceTitle}“ → „{t.NewTitle}“");

    public string Text
    {
        get
        {
            var parts = new List<string> { Tracks.Count == 0 ? L.T("kein Ton", "no audio") : string.Join(" | ", Tracks.Select(t => t.Text)) };
            if (MovedFirst) parts.Add(L.T("Standardspur an erster Stelle", "default track first"));
            if (Removed.Count > 0) parts.Add(L.T("entfernt: ", "removed: ") + string.Join(", ", Removed));
            if (TitleChanges.Any()) parts.Add(L.T("Spurtitel (nannte das alte Format): ", "track title (named the old format): ") + string.Join(", ", TitleChanges));
            if (Note.Length > 0) parts.Add(Note);
            return string.Join(" · ", parts);
        }
    }

    public IEnumerable<string> Args()
    {
        foreach (var t in Tracks)
        {
            var i = t.OutputIndex;
            if (t.Convert && t.Codec == "flac")
            {
                yield return $"-c:a:{i}"; yield return "flac";
            }
            else if (t.Convert)
            {
                yield return $"-c:a:{i}"; yield return t.Codec;
                yield return $"-b:a:{i}"; yield return $"{t.BitrateKbit}k";
                yield return $"-ar:a:{i}"; yield return "48000";
                if (t.OutChannels == 6 && t.SourceChannels > 6) { yield return $"-ac:a:{i}"; yield return "6"; }
            }
            if (t.Disposition is not null) { yield return $"-disposition:a:{i}"; yield return t.Disposition; }
            if (t.NewTitle is not null) { yield return $"-metadata:s:a:{i}"; yield return "title=" + t.NewTitle; }
        }
    }
}

public sealed record SubtitleTrackPlan(int OutputIndex, int SourceIndex, string SourceText)
{
    public string? Codec { get; init; }
    public string? Disposition { get; init; }
    public bool Forced { get; init; }
    public bool DefaultRemoved { get; init; }

    public string Text => SourceText + (Codec is not null ? $" → {Codec.ToUpperInvariant()}" : "") + (Forced ? L.T(" (erzwungen)", " (forced)") : "");
}

public sealed record SubtitlePlan(IReadOnlyList<SubtitleTrackPlan> Tracks)
{
    public static readonly SubtitlePlan Empty = new([]);

    public IReadOnlyList<string> Removed { get; init; } = [];

    public IReadOnlyList<string> Unsupported { get; init; } = [];
    public bool AllMapped { get; init; } = true;

    public bool Changed => Removed.Count > 0 || Tracks.Any(t => t.Codec is not null || t.Disposition is not null);

    public string Text
    {
        get
        {
            var parts = new List<string> { Tracks.Count == 0 ? L.T("keine", "none") : string.Join(" | ", Tracks.Select(t => t.Text)) };
            if (Removed.Count > 0) parts.Add(L.T("entfernt: ", "removed: ") + string.Join(", ", Removed));
            var noDefault = Tracks.Where(t => t.DefaultRemoved).Select(t => t.SourceText).ToList();
            if (noDefault.Count > 0)
                parts.Add(L.T($"nicht mehr als Standard markiert: {string.Join(", ", noDefault)} (Bild-Untertitel: der Medienserver müsste sonst das ganze Video umrechnen)",
                              $"no longer flagged as default: {string.Join(", ", noDefault)} (image subtitles: the media server would otherwise have to convert the whole video)"));
            return string.Join(" · ", parts);
        }
    }

    public IEnumerable<string> Args()
    {
        foreach (var t in Tracks)
        {
            if (t.Codec is not null) { yield return $"-c:s:{t.OutputIndex}"; yield return t.Codec; }
            if (t.Disposition is not null) { yield return $"-disposition:s:{t.OutputIndex}"; yield return t.Disposition; }
        }
    }
}

public static partial class AudioPlanner
{
    [GeneratedRegex(@"kommentar|commentary|director'?s comment|audiokommentar", RegexOptions.IgnoreCase)]
    private static partial Regex CommentaryTitle();

    [GeneratedRegex(@"audiodeskription|h(ö|oe)rfilm|audio ?description|\bAD\b|sehbehindert", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionTitle();

    [GeneratedRegex(@"forced|erzwungen", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedTitle();

    public static bool IsLossless(StreamInfo s) =>
        s.CodecName is "truehd" or "mlp" or "flac" or "alac" or "s302m"
        || s.CodecName.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
        || (s.CodecName == "dts" && s.Profile.StartsWith("DTS-HD", StringComparison.OrdinalIgnoreCase));

    public static bool IsTvFriendly(StreamInfo s) => s.CodecName is "aac" or "ac3" or "eac3" or "mp3" or "mp2";

    private static bool CanDecode(StreamInfo s) => s.CodecName.Length > 0 && s.CodecName is not ("none" or "unknown" or "ac4" or "mpegh_3d_audio");

    public static bool ShouldConvert(StreamInfo s, Profile p) =>
        IsLossless(s) ? p.ConvertLosslessAudio : p.ConvertDts && !IsTvFriendly(s) && CanDecode(s);

    public static bool IsCommentary(StreamInfo s) => s.IsComment || CommentaryTitle().IsMatch(s.Title);
    public static bool IsAudioDescription(StreamInfo s) => s.IsVisualImpaired || DescriptionTitle().IsMatch(s.Title);
    public static bool IsForcedSubtitle(StreamInfo s) => s.IsForced || ForcedTitle().IsMatch(s.Title);

    public static string ChannelText(int c) => c switch
    {
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        <= 0 => "?",
        _ => c + L.T(" Kanäle", " channels"),
    };

    private static string Describe(StreamInfo a)
    {
        var name = a.CodecName switch
        {
            "truehd" => "TrueHD",
            "dts" when !string.IsNullOrEmpty(a.Profile) => a.Profile,
            "hdmv_pgs_subtitle" => "PGS",
            "dvd_subtitle" => "VobSub",
            "subrip" => "SRT",
            "mov_text" => "MP4-Text",
            _ => a.CodecName.ToUpperInvariant(),
        };
        var lang = Languages.Display(a.Language);
        var text = (lang.Length > 0 ? lang + " " : "") + name;
        if (a.CodecType == "audio") text += " " + ChannelText(a.Channels);
        return text;
    }

    public static string CurrentDisposition(StreamInfo s) => Disposition(s.IsDefault, s.IsForced, s.IsComment, s.IsVisualImpaired, s.IsHearingImpaired);

    private static string Disposition(bool isDefault, bool forced, bool comment, bool visual, bool hearing)
    {
        var p = new List<string>();
        if (isDefault) p.Add("default");
        if (forced) p.Add("forced");
        if (comment) p.Add("comment");
        if (visual) p.Add("visual_impaired");
        if (hearing) p.Add("hearing_impaired");
        return p.Count == 0 ? "0" : string.Join("+", p);
    }

    // ---------------- Ton ----------------

    public static AudioPlan Plan(MediaInfo media, Profile profile)
    {
        var all = media.Audio.ToList();
        var keep = all.ToList();
        var removed = new List<string>();
        var note = "";

        var langs = Languages.ParseList(profile.AudioLanguages);
        if (langs.Count > 0)
        {
            var filtered = keep.Where(a => Languages.Allowed(a.Language, langs)).ToList();
            if (filtered.Count == 0) note = L.T("keine Spur in den gewünschten Sprachen, alle behalten", "no track in the wanted languages, all kept");
            else
            {
                removed.AddRange(keep.Except(filtered).Select(a => Describe(a)));
                keep = filtered;
            }
        }
        void Drop(Func<StreamInfo, bool> match, string why)
        {
            var drop = keep.Where(match).ToList();
            if (drop.Count == 0 || drop.Count == keep.Count) return;
            removed.AddRange(drop.Select(a => $"{Describe(a)} ({why})"));
            keep = keep.Except(drop).ToList();
        }
        if (profile.RemoveCommentary) Drop(IsCommentary, L.T("Kommentar", "commentary"));
        if (profile.RemoveAudioDescription) Drop(IsAudioDescription, L.T("Audiodeskription", "audio description"));

        StreamInfo? def = null;
        if (!string.IsNullOrWhiteSpace(profile.DefaultAudioLanguage))
        {
            def = keep.FirstOrDefault(a => Languages.Matches(a.Language, profile.DefaultAudioLanguage) && !IsCommentary(a) && !IsAudioDescription(a))
                  ?? keep.FirstOrDefault(a => Languages.Matches(a.Language, profile.DefaultAudioLanguage));
        }
        var movedFirst = def is not null && keep[0] != def;
        if (movedFirst)
        {
            keep.Remove(def!);
            keep.Insert(0, def!);
        }

        var ac3 = profile.AudioTarget == AudioTarget.Ac3;
        var tracks = new List<AudioTrackPlan>();
        var i = 0;
        foreach (var a in keep)
        {
            var c = a.Channels;
            var lossless = IsLossless(a);
            var convert = ShouldConvert(a, profile);
            string? disp = def is null ? null
                : Disposition(a == def, a.IsForced, a.IsComment, a.IsVisualImpaired, a.IsHearingImpaired);
            if (convert)
            {
                var br = c switch
                {
                    >= 6 => profile.AudioBitrateKbit,
                    >= 3 => 448,
                    2 => 256,
                    1 => 128,
                    _ => profile.AudioBitrateKbit,
                };
                if (ac3) br = Math.Min(br, 640);
                var codec = ac3 ? "ac3" : "eac3";
                tracks.Add(new AudioTrackPlan(i, Describe(a), true, c > 6 ? 6 : c, br)
                {
                    SourceIndex = a.Index, SourceChannels = c, Lossless = lossless, Codec = codec, Disposition = disp,
                    SourceTitle = a.Title, NewTitle = TrackTitles.CleanAudio(a.Title, a.CodecName, codec, c > 6),
                });
            }
            else if (Converting.ConvertPlanner.MkvAudioToFlac.Contains(a.CodecName))
            {
                tracks.Add(new AudioTrackPlan(i, Describe(a), true, c, 0)
                {
                    SourceIndex = a.Index, SourceChannels = c, Lossless = true, Codec = "flac", Disposition = disp,
                    SourceTitle = a.Title, NewTitle = TrackTitles.CleanAudio(a.Title, a.CodecName, "flac", false),
                });
            }
            else
            {
                tracks.Add(new AudioTrackPlan(i, Describe(a), false, c, 0) { SourceIndex = a.Index, SourceChannels = c, Lossless = lossless, Disposition = disp });
            }
            i++;
        }
        return new AudioPlan(tracks) { Removed = removed, AllMapped = keep.SequenceEqual(all), MovedFirst = movedFirst, Note = note };
    }

    // ---------------- Untertitel ----------------

    public static SubtitlePlan PlanSubtitles(MediaInfo media, Profile profile)
    {
        var all = media.Subtitles.ToList();
        var keep = all.ToList();
        var removed = new List<string>();

        var langs = Languages.ParseList(profile.SubtitleLanguages);
        if (langs.Count > 0)
        {
            var drop = keep.Where(s => !Languages.Allowed(s.Language, langs)).ToList();
            removed.AddRange(drop.Select(s => Describe(s)));
            keep = keep.Except(drop).ToList();
        }
        if (profile.RemoveImageSubtitles)
        {
            var drop = keep.Where(s => s.IsImageSubtitle && !IsForcedSubtitle(s)).ToList();
            removed.AddRange(drop.Select(s => L.T($"{Describe(s)} (Bild)", $"{Describe(s)} (image)")));
            keep = keep.Except(drop).ToList();
        }

        var unsupported = new List<string>();
        {
            var drop = keep.Where(s => !Converting.ConvertPlanner.MkvSubs.Contains(s.CodecName) && !Converting.ConvertPlanner.TextSubs.Contains(s.CodecName)).ToList();
            unsupported.AddRange(drop.Select(s => L.T($"{Describe(s)} ({s.CodecName}, in MKV nicht möglich)", $"{Describe(s)} ({s.CodecName}, not possible in MKV)")));
            removed.AddRange(unsupported);
            keep = keep.Except(drop).ToList();
        }

        var auto = profile.ForcedSubtitles == ForcedSubtitleMode.Auto && keep.Any(IsForcedSubtitle);
        var firstText = profile.ForcedSubtitles == ForcedSubtitleMode.First ? keep.FirstOrDefault(s => !s.IsImageSubtitle) : null;
        var tracks = new List<SubtitleTrackPlan>();
        var i = 0;
        foreach (var s in keep)
        {
            var image = s.IsImageSubtitle;
            bool forced;
            string? disp = null;
            if (profile.ForcedSubtitles == ForcedSubtitleMode.First)
            {
                forced = image ? s.IsForced : s == firstText;
                disp = Disposition(false, forced, false, false, false);
            }
            else if (auto)
            {
                forced = IsForcedSubtitle(s);
                var isDefault = !image && (forced ? profile.ForcedSubtitlesDefault || s.IsDefault : !profile.ForcedSubtitlesDefault && s.IsDefault);
                disp = Disposition(isDefault, forced, s.IsComment, s.IsVisualImpaired, s.IsHearingImpaired);
            }
            else
            {
                forced = s.IsForced;
                if (image && s.IsDefault) disp = Disposition(false, s.IsForced, s.IsComment, s.IsVisualImpaired, s.IsHearingImpaired);
            }
            tracks.Add(new SubtitleTrackPlan(i, s.Index, Describe(s))
            {
                Codec = Converting.ConvertPlanner.MkvSubs.Contains(s.CodecName) ? null : "srt",
                Disposition = disp,
                Forced = forced,
                DefaultRemoved = image && s.IsDefault,
            });
            i++;
        }
        return new SubtitlePlan(tracks) { Removed = removed, Unsupported = unsupported, AllMapped = keep.Count == all.Count };
    }

    public static List<int> PlanAttachments(MediaInfo media, Profile profile) =>
        profile.KeepAttachments ? media.Attachments.Where(a => a.MimeType.Length > 0).Select(a => a.Index).ToList() : [];
}
