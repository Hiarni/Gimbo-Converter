namespace FilmKonverter.Core.Jobs;

public enum AutoPauseAction
{
    None,
    Pause,
    Resume,
}

public sealed class AutoPauseState
{
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(30);

    private DateTime _freeSince = DateTime.MinValue;

    public bool AutoPaused { get; private set; }

    public bool ManualOverride { get; private set; }

    public AutoPauseAction Decide(bool enabled, bool running, bool paused, bool busy, DateTime now)
    {
        if (!running)
        {
            Reset();
            return AutoPauseAction.None;
        }
        if (AutoPaused && !paused) AutoPaused = false;
        if (!enabled)
        {
            ManualOverride = false;
            _freeSince = DateTime.MinValue;
            return AutoPaused ? AutoPauseAction.Resume : AutoPauseAction.None;
        }
        if (busy)
        {
            _freeSince = DateTime.MinValue;
            return !paused && !ManualOverride ? AutoPauseAction.Pause : AutoPauseAction.None;
        }
        ManualOverride = false;
        if (!AutoPaused) return AutoPauseAction.None;
        if (_freeSince == DateTime.MinValue)
        {
            _freeSince = now;
            return AutoPauseAction.None;
        }
        return now - _freeSince >= ResumeDelay ? AutoPauseAction.Resume : AutoPauseAction.None;
    }

    public void MarkPaused() => AutoPaused = true;

    public void MarkResumed()
    {
        AutoPaused = false;
        _freeSince = DateTime.MinValue;
    }

    public void UserToggled()
    {
        if (AutoPaused) ManualOverride = true;
        AutoPaused = false;
        _freeSince = DateTime.MinValue;
    }

    public void Reset()
    {
        AutoPaused = false;
        ManualOverride = false;
        _freeSince = DateTime.MinValue;
    }
}
