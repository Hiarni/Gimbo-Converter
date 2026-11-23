namespace FilmKonverter.App.Pages;

public partial class FfmpegPage
{
    public FfmpegPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Ffmpeg;
        AppState.Instance.Ffmpeg.Refresh();
        Loaded += async (_, _) => await AppState.Instance.Ffmpeg.CheckOnOpenAsync();
    }
}
