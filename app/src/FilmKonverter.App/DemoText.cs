using FilmKonverter.Core.Util;

namespace FilmKonverter.App;

internal static class DemoText
{
    private static readonly (string De, string En)[] Words =
    [
        ("Neuer Anfang", "New Beginning"), ("Der.Anruf", "The.Call"), ("Der Anruf", "The Call"), ("Heimkehr", "Homecoming"),
        ("Neue Wege", "New Paths"), ("Das Ende", "The End"), ("Der Fremde", "The Stranger"), ("Teil.Eins", "Part.One"), ("Teil Eins", "Part One"),
        ("Komprimiert-Filme", "Compressed-Movies"), ("Komprimiert", "Compressed"), ("Konvertiert", "Converted"), ("Serien", "TV"), ("Filme", "Movies"),
        ("Sammlung", "Collection"), ("Archiv", "Archive"), ("Musik", "Music"), ("Staffel ", "Season "), ("Serie ", "Series "), ("Serie.", "Series."),
        ("Folge ", "Episode "),
    ];

    public static string T(string text)
    {
        if (L.German) return text;
        foreach (var (de, en) in Words) text = text.Replace(de, en);
        return text;
    }
}
