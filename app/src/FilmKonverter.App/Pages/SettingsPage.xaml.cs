namespace FilmKonverter.App.Pages;

public partial class SettingsPage
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.SettingsPage;
        AppState.Instance.SettingsPage.RefreshStats();
    }
}
