using System.Globalization;

namespace FilmKonverter.Core.Util;

public enum UiLanguage
{
    Auto,
    German,
    English,
}

public static class L
{
    private static readonly CultureInfo DeCulture = CultureInfo.GetCultureInfo("de-AT");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-GB");

    public static bool German { get; private set; } = Detect();

    public static CultureInfo Culture => German ? DeCulture : EnCulture;

    public static string T(string de, string en) => German ? de : en;

    public static void Set(UiLanguage lang) => German = lang switch
    {
        UiLanguage.German => true,
        UiLanguage.English => false,
        _ => Detect(),
    };

    public static bool Detect()
    {
        try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
