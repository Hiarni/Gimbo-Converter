namespace FilmKonverter.App.Pages;

public partial class StatsPage
{
    public StatsPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Statistics;
        Loaded += async (_, _) =>
        {
            if (App.ScreenshotFolder is null && AppState.Instance.Statistics.RefreshCommand.CanExecute(null))
                await AppState.Instance.Statistics.RefreshAsync();
        };
    }
}
