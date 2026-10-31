using System.Runtime.ExceptionServices;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

/// <summary>Deterministic harness for MarketDataClient: fake clock, fake transport, single-threaded
/// context on the test thread. Time only moves through Advance/AdvanceTo, which fire due timers in order
/// and drain all continuations after each one.</summary>
internal sealed class Sim : IDisposable
{
    private const int MaxTimerFirings = 3_000_000;
    private readonly SynchronizationContext? previousContext;
    private readonly CancellationTokenSource cts = new();
    private Task? run;

    public Sim(DateTimeOffset? start = null, Func<double>? jitter = null)
    {
        previousContext = SynchronizationContext.Current;
        Context = new DeterministicContext();
        SynchronizationContext.SetSynchronizationContext(Context);
        try
        {
            Time = new FakeTimeProvider(start ?? Fixtures.RecordedAt);
            Transport = new FakeTransport(Time);
            Client = new MarketDataClient(Transport, Time, jitter ?? (() => 0.5));
        }
        catch
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            throw;
        }
    }

    public FakeTimeProvider Time { get; }

    public FakeTransport Transport { get; }

    public DeterministicContext Context { get; }

    public MarketDataClient Client { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public DateTimeOffset Start { get; private set; }

    public void Run()
    {
        Start = Now;
        run = Client.RunAsync(cts.Token);
        Drain();
    }

    public void Drain()
    {
        Context.RunUntilIdle();
        if (run is { IsFaulted: true })
        {
            ExceptionDispatchInfo.Capture(run.Exception!.GetBaseException()).Throw();
        }
    }

    public void Advance(TimeSpan by) => AdvanceTo(Now + by);

    public void AdvanceTo(DateTimeOffset target)
    {
        Drain();
        for (var firings = 0; ; firings++)
        {
            if (firings > MaxTimerFirings)
            {
                throw new InvalidOperationException("Too many timer firings: the client busy-waits on tiny delays.");
            }

            var timer = Time.NextDue(target);
            if (timer is null)
            {
                break;
            }

            Time.SetNow(timer.Due!.Value);
            timer.Fire();
            Drain();
        }

        Time.SetNow(target);
        Drain();
    }

    /// <summary>Advances to <paramref name="target"/> in steps; after each step pushes the message to the open
    /// socket of the exchange, if there is one (a live exchange pushes data every few seconds).</summary>
    public void AdvanceFeeding(Exchange exchange, DateTimeOffset target, TimeSpan every, string message)
    {
        while (Now < target)
        {
            var next = Now + every;
            AdvanceTo(next < target ? next : target);
            var socket = Transport.OpenSocket(exchange);
            if (socket is not null)
            {
                socket.Push(message);
                Drain();
            }
        }
    }

    /// <summary>Pushes a message to the open socket of the exchange (fails if there is none).</summary>
    public void Push(Exchange exchange, string message)
    {
        var socket = Transport.OpenSocket(exchange)
            ?? throw new Xunit.Sdk.XunitException($"no open {exchange} socket at {Now:O}");
        socket.Push(message);
        Drain();
    }

    public static MarketSource ToSource(Exchange exchange) =>
        exchange == Exchange.Binance ? MarketSource.Binance : MarketSource.Kraken;

    public void Dispose()
    {
        try
        {
            cts.Cancel();
            Context.RunUntilIdle();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            cts.Dispose();
        }
    }
}
