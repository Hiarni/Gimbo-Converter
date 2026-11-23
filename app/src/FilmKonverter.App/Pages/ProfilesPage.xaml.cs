namespace FilmKonverter.App.Pages;

public partial class ProfilesPage
{
    public ProfilesPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Profiles;
    }
}
