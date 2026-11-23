using System.Windows.Markup;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App;

[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T()
    {
    }

    public T(string de, string en)
    {
        De = de;
        En = en;
    }

    [ConstructorArgument("de")]
    public string De { get; set; } = "";

    [ConstructorArgument("en")]
    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(De, En);
}
