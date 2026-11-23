using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;
using Xunit;

namespace FilmKonverter.Core.Tests;

public class FullscreenRulesTests
{
    [Theory]
    [InlineData("mstsc")]
    [InlineData("MSTSC")]
    [InlineData("mstsc.exe")]
    [InlineData(@"C:\Windows\System32\mstsc.exe")]
    [InlineData("msrdc")]
    [InlineData("vmconnect")]
    [InlineData("AnyDesk")]
    [InlineData("TeamViewer")]
    [InlineData("rustdesk")]
    [InlineData("VirtualBoxVM")]
    public void Fernwartung_und_VMs_pausieren_nicht(string process) => Assert.True(FullscreenRules.IsIgnored(process, ""));

    [Theory]
    [InlineData("vlc")]
    [InlineData("chrome")]
    [InlineData("eldenring")]
    [InlineData("POWERPNT")]
    [InlineData("explorer")]
    [InlineData("mstsc2")]
    public void Spiele_Filme_und_alles_andere_pausieren_weiter(string process) => Assert.False(FullscreenRules.IsIgnored(process, ""));

    [Fact]
    public void Unbekannter_Prozess_pausiert_weiter()
    {
        Assert.False(FullscreenRules.IsIgnored("", "chrome"));
        Assert.False(FullscreenRules.IsIgnored(null, "chrome"));
    }

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome", "chrome.exe")]
    [InlineData("chrome", "vlc, chrome")]
    [InlineData("chrome", "vlc;CHROME")]
    [InlineData("chrome", "vlc\r\n\"chrome.exe\"")]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("parsecd", "parsec*")]
    [InlineData("vmware-vmx", "*vmx")]
    [InlineData("MyRemoteTool64", "*remote*")]
    public void Eigene_Ausnahmen_greifen(string process, string list) => Assert.True(FullscreenRules.IsIgnored(process, list));

    [Theory]
    [InlineData("vlc", "chrome")]
    [InlineData("chromium", "chrome")]
    [InlineData("vlc", "")]
    [InlineData("vlc", null)]
    [InlineData("vlc", " , ;; ")]
    [InlineData("vlc", "*")]
    [InlineData("vlc", "**")]
    public void Eigene_Ausnahmen_greifen_nur_beim_genannten_Programm(string process, string? list) => Assert.False(FullscreenRules.IsIgnored(process, list));

    [Fact]
    public void Liste_wird_bereinigt_gespeichert()
    {
        Assert.Equal("chrome, VLC", FullscreenRules.Clean("  chrome.exe ;; VLC.EXE , Chrome\n"));
        Assert.Equal("", FullscreenRules.Clean(null));
        Assert.Equal("", FullscreenRules.Clean(" , ; "));
    }

    [Fact]
    public void Alte_Einstellungen_ohne_das_Feld_bleiben_gueltig()
    {
        var s = new AppSettings { FullscreenIgnore = null! };
        s.Normalize();
        Assert.Equal("", s.FullscreenIgnore);
        Assert.True(s.PauseWhenFullscreen);
    }
}
