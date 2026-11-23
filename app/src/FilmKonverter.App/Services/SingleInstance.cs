namespace FilmKonverter.App.Services;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\GimboKonverter.Instanz";
    private const string EventName = @"Local\GimboKonverter.Anzeigen";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _event;
    private readonly bool _owner;
    private RegisteredWaitHandle? _wait;

    public SingleInstance(TimeSpan wait = default)
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            _owner = _mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            _owner = true;
        }
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirst => _owner;

    public void SignalFirst()
    {
        try { _event?.Set(); } catch { }
    }

    public void OnShowRequested(Action action)
    {
        if (_event is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event?.Dispose();
        if (_owner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
