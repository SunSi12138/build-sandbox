namespace StreetEcoTool;

/// <summary>A local-midnight timer, recreated from the clock after every wake-up.</summary>
public sealed class DailyMaintenanceScheduler : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Action<DateOnly> _run;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private readonly ITimer _timer;
    private bool _disposed;

    public DailyMaintenanceScheduler(TimeProvider time, Action<DateOnly> run, Action<Exception>? onError = null)
    {
        _time = time;
        _run = run;
        _onError = onError;
        _timer = time.CreateTimer(_ => Wake(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Schedule();
    }

    public static TimeSpan UntilNextMidnight(TimeProvider time)
    {
        var now = time.GetUtcNow();
        var nextLocal = time.GetLocalNow().Date.AddDays(1);
        while (time.LocalTimeZone.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddMinutes(1);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), time.LocalTimeZone);
        var delay = nextUtc - now.UtcDateTime;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
    }

    private void Schedule() => _timer.Change(UntilNextMidnight(_time), Timeout.InfiniteTimeSpan);
    private void Wake()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _run(DateOnly.FromDateTime(_time.GetLocalNow().Date)); }
            catch (Exception error) { _onError?.Invoke(error); }
            finally { if (!_disposed) Schedule(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
