using System.Runtime.CompilerServices;
using FilmKonverter.Core.Library;

namespace FilmKonverter.Core.Tests;

internal static class DataFolderSetup
{
    public static readonly string Base = Path.Combine(Path.GetTempPath(), "gimbo-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Init()
    {
        DataStore.Configure(Path.Combine(Base, "Daten"), Path.Combine(Base, "Test"), share: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Base, recursive: true); } catch { }
        };
    }
}
