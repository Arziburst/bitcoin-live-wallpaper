namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

/// <summary>A manual clock: time moves only when the test advances it; timers fire in due order on the
/// test thread (see <see cref="Sim.AdvanceTo"/>). Serves Task.Delay(delay, provider, ct), PeriodicTimer,
/// CancellationTokenSource(delay, provider) and anything else built on CreateTimer.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now;
    private long sequence;

    public FakeTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => now.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (timers)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>The armed timer due first at or before <paramref name="limit"/>, or null.</summary>
    internal FakeTimer? NextDue(DateTimeOffset limit)
    {
        lock (timers)
        {
            timers.RemoveAll(t => t.IsDisposed);
            return timers
                .Where(t => t.Due is { } due && due <= limit)
                .OrderBy(t => t.Due)
                .ThenBy(t => t.Sequence)
                .FirstOrDefault();
        }
    }

    internal void SetNow(DateTimeOffset value)
    {
        if (value > now)
        {
            now = value;
        }
    }

    internal long NextSequence() => Interlocked.Increment(ref sequence);

    internal sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public long Sequence { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            this.period = period;
            Sequence = owner.NextSequence();
            Due = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            return true;
        }

        public void Fire()
        {
            if (IsDisposed || Due is null)
            {
                return;
            }

            if (period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
            {
                Due = Due.Value + period;
                Sequence = owner.NextSequence();
            }
            else
            {
                Due = null;
            }

            callback(state);
        }

        public void Dispose()
        {
            IsDisposed = true;
            Due = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A single-threaded SynchronizationContext: posted continuations queue up and run only when the
/// test drains the queue, so the client under test is fully deterministic.</summary>
internal sealed class DeterministicContext : SynchronizationContext
{
    private const int MaxStepsPerDrain = 1_000_000;
    private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (queue)
        {
            queue.Enqueue((d, state));
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    public void RunUntilIdle()
    {
        for (var steps = 0; ; steps++)
        {
            if (steps > MaxStepsPerDrain)
            {
                throw new InvalidOperationException(
                    "The client never goes idle (a busy loop without awaiting the TimeProvider or the transport?).");
            }

            (SendOrPostCallback Callback, object? State) item;
            lock (queue)
            {
                if (!queue.TryDequeue(out item))
                {
                    return;
                }
            }

            item.Callback(item.State);
        }
    }
}
