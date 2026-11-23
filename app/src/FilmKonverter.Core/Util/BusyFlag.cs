namespace FilmKonverter.Core.Util;

public sealed class BusyFlag
{
    private int _busy;
    private Action? _next;

    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void Exit() => Volatile.Write(ref _busy, 0);

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public void Run(Action work)
    {
        Interlocked.Exchange(ref _next, work);
        if (!TryEnter()) return;
        _ = Task.Run(() =>
        {
            while (true)
            {
                while (Interlocked.Exchange(ref _next, null) is { } job)
                {
                    try { job(); } catch { }
                }
                Exit();
                if (Volatile.Read(ref _next) is null || !TryEnter()) return;
            }
        });
    }
}
