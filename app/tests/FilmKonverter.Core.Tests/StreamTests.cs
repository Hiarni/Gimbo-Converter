using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class StreamTests(TestMedia m)
{
    [Theory]
    [InlineData("de", "ger")]
    [InlineData("Deutsch", "ger")]
    [InlineData("deu", "ger")]
    [InlineData("EN", "eng")]
    [InlineData("xyz", "xyz")]
    public void Sprachen_werden_vereinheitlicht(string input, string expected) => Assert.Equal(expected, Languages.Normalize(input));

    [Fact]
    public void Unbekannte_Sprache_passt_immer()
    {
        var list = Languages.ParseList("de, en");
        Assert.True(Languages.Allowed("und", list));
        Assert.True(Languages.Allowed("", list));
        Assert.True(Languages.Allowed("deu", list));
        Assert.False(Languages.Allowed("fre", list));
        Assert.True(Languages.Allowed("fre", Languages.ParseList("")));
    }

    [Fact]
    public async Task Analyse_liest_Sprache_Titel_und_Kennzeichen()
    {
        var info = (await m.Tools.ProbeAsync(m.Multi, CancellationToken.None))!;
        var a = info.Audio.ToList();
        Assert.Equal(4, a.Count);
        Assert.Equal("dts", a[1].CodecName);
        Assert.True(a[1].IsDefault);
        Assert.True(AudioPlanner.IsCommentary(a[2]));
        Assert.True(AudioPlanner.IsAudioDescription(a[3]));
        Assert.True(AudioPlanner.IsForcedSubtitle(info.Subtitles.First()));
        Assert.Single(info.Attachments);
    }

    [Fact]
    public async Task Plan_filtert_Sprachen_Kommentar_und_setzt_Standard()
    {
        var info = (await m.Tools.ProbeAsync(m.Multi, CancellationToken.None))!;
        var p = Profile.Nvidia();
        p.AudioLanguages = "de";
        p.RemoveCommentary = true;
        p.DefaultAudioLanguage = "deutsch";
        var plan = AudioPlanner.Plan(info, p);
        Assert.False(plan.AllMapped);
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal("default", plan.Tracks[0].Disposition);
        Assert.Equal("visual_impaired", plan.Tracks[1].Disposition);
        Assert.Equal(2, plan.Removed.Count);

        p.AudioLanguages = "fr";
        p.RemoveCommentary = false;
        var all = AudioPlanner.Plan(info, p);
        Assert.Equal(4, all.Tracks.Count);
        Assert.True(all.AllMapped);
        Assert.NotEmpty(all.Note);
    }

    [Fact]
    public async Task Untertitel_Plan_erzwungen_automatisch()
    {
        var info = (await m.Tools.ProbeAsync(m.Multi, CancellationToken.None))!;
        var p = Profile.Nvidia();
        p.SubtitleLanguages = "de";
        p.ForcedSubtitlesDefault = true;
        var plan = AudioPlanner.PlanSubtitles(info, p);
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal("default+forced", plan.Tracks[0].Disposition);
        Assert.Equal("0", plan.Tracks[1].Disposition);

        p.ForcedSubtitles = ForcedSubtitleMode.Keep;
        Assert.All(AudioPlanner.PlanSubtitles(info, p).Tracks, t => Assert.Null(t.Disposition));
    }

    private async Task<(Recorder Rec, string Output)> Encode(string source, Action<Profile> configure, string ext = ".mkv")
    {
        var root = Path.Combine(m.Dir, "st-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "in");
        Directory.CreateDirectory(input);
        File.Copy(source, Path.Combine(input, "Datei" + ext));
        var p = Profile.Cpu();
        p.InputDir = input;
        p.OutputDir = Path.Combine(root, "out");
        p.CpuPreset = "ultrafast";
        p.Quality = 30;
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        p.MinFreeSpaceGb = 0;
        p.EarlyAbortPercent = 0;
        configure(p);
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec).RunAsync(CancellationToken.None);
        return (rec, Path.Combine(p.OutputDir, "Datei.mkv"));
    }

    [Fact]
    public async Task Kodieren_mit_Filtern_Kennzeichen_und_Anhang()
    {
        var (rec, output) = await Encode(m.Multi, p =>
        {
            p.AudioLanguages = "de";
            p.RemoveCommentary = true;
            p.DefaultAudioLanguage = "de";
            p.SubtitleLanguages = "de";
            p.ForcedSubtitlesDefault = true;
        });
        var end = rec.EndOf("Datei");
        Assert.True(end.Outcome is Outcome.Encoded, end.Message + " | " + string.Join(" / ", rec.Lines.Select(l => l.Text)));
        if (end.Outcome == Outcome.Skipped) return;
        var o = (await m.Tools.ProbeAsync(output, CancellationToken.None))!;
        var a = o.Audio.ToList();
        Assert.Equal(2, a.Count);
        Assert.True(a[0].IsDefault);
        Assert.False(a[1].IsDefault);
        Assert.True(a[1].IsVisualImpaired);
        var s = o.Subtitles.ToList();
        Assert.Equal(2, s.Count);
        Assert.True(s[0].IsForced && s[0].IsDefault);
        Assert.False(s[1].IsForced);
        Assert.Single(o.Attachments);
        Assert.Contains(rec.Lines, l => l.Label == "Ton" && l.Text.Contains("entfernt"));
    }

    [Fact]
    public async Task DTS_wird_umgewandelt()
    {
        var (rec, output) = await Encode(m.Multi, p => { p.ConvertDts = true; p.AudioTarget = AudioTarget.Ac3; p.KeepAttachments = false; });
        var end = rec.EndOf("Datei");
        Assert.True(end.Outcome is Outcome.Encoded, end.Message);
        Assert.Contains(rec.Lines, l => l.Label == "Ton" && l.Text.Contains("→ AC3"));
        if (end.Outcome == Outcome.Skipped) return;
        var o = (await m.Tools.ProbeAsync(output, CancellationToken.None))!;
        Assert.Equal(new[] { "ac3", "ac3", "ac3", "ac3" }, o.Audio.Select(x => x.CodecName).ToArray());
        Assert.Empty(o.Attachments);
    }

    [Fact]
    public async Task MP4_Untertitel_werden_zu_SRT()
    {
        var (rec, output) = await Encode(m.Mp4Text, _ => { }, ".mp4");
        var end = rec.EndOf("Datei");
        Assert.True(end.Outcome is Outcome.Encoded, end.Message + " | " + string.Join(" / ", rec.Lines.Select(l => l.Text)));
        Assert.Contains(rec.Lines, l => l.Label == "Untertitel" && l.Text.Contains("SRT"));
        if (end.Outcome == Outcome.Skipped) return;
        var o = (await m.Tools.ProbeAsync(output, CancellationToken.None))!;
        Assert.Equal("subrip", Assert.Single(o.Subtitles).CodecName);
    }
}
