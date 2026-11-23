namespace FilmKonverter.App.Pages;

public partial class ToolsPage
{
    public ToolsPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.ToolsPage;
    }
}
