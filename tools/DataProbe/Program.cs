using System.Globalization;
using BitcoinWallpaper.Market.Data;

// Live probe: runs the real client against the exchanges and prints 5 lines of BTC data, or fails after 30 s.
const int Lines = 5;
var limit = TimeSpan.FromSeconds(30);

using var transport = new HttpMarketTransport();
var client = new MarketDataClient(transport, TimeProvider.System, Random.Shared.NextDouble);
using var stop = new CancellationTokenSource();
var run = client.RunAsync(stop.Token);

var started = TimeProvider.System.GetTimestamp();
string? previous = null;
var printed = 0;
using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
while (printed < Lines && TimeProvider.System.GetElapsedTime(started) < limit && !run.IsCompleted)
{
    await tick.WaitForNextTickAsync();
    var candles = client.Candles.Snapshot();
    var ticker = client.Ticker;
    if (candles.Count == 0 || ticker is null)
    {
        continue;
    }

    var last = candles[^1];
    var line = string.Create(
        CultureInfo.InvariantCulture,
        $"{client.Source} BTC {ticker.Last:0.00} 24h {ticker.ChangeAbs:+0.00;-0.00} ({ticker.ChangePercent:+0.00;-0.00}%) " +
        $"H {ticker.High:0.00} L {ticker.Low:0.00} V {ticker.Volume:0.###} | 5m {last.OpenTime:HH:mm}Z " +
        $"O {last.Open:0.00} H {last.High:0.00} L {last.Low:0.00} C {last.Close:0.00} V {last.Volume:0.#####} " +
        $"{(last.IsClosed ? "closed" : "forming")} | {candles.Count} candles, {client.State}");
    if (line != previous)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}Z {line}");
        previous = line;
        printed++;
    }
}

stop.Cancel();
try
{
    await run;
}
catch (OperationCanceledException)
{
}

if (printed < Lines)
{
    Console.Error.WriteLine($"only {printed} of {Lines} lines of live data within {limit.TotalSeconds:0} s");
    return 1;
}

return 0;
