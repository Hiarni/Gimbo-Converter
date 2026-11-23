namespace FilmKonverter.App.Pages;

public partial class LibraryPage
{
    public LibraryPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Library;
        Loaded += async (_, _) =>
        {
            if (App.ScreenshotFolder is null && AppState.Instance.Library.RefreshProblemsCommand.CanExecute(null))
                await AppState.Instance.Library.RefreshProblemsAsync();
        };
    }
}
