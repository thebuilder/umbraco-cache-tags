namespace TheBuilder.CacheTags.Tests;

/// <summary>A clock that only moves when a test moves it, firing the timers (delays, timeouts) that fall due.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(timer => timer.DueAt <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // One-shot: the package's delays and timeouts never repeat.
    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                DueAt = time._now + dueTime;
                time._timers.Add(this);
            }

            if (dueTime == TimeSpan.Zero)
            {
                time.Advance(TimeSpan.Zero);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
