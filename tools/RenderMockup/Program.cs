using System.Globalization;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Tools.RenderMockup;

// Renders the chart to PNG at the owner's two screens, headless (WIC bitmap, software Direct2D, no window).
//   --out <dir>        where the PNGs go (default: mockups)
//   --offline          from the recorded snapshot in tests/Chart.Render.Tests/Fixtures instead of live Binance REST
//   --fixtures <dir>   another recorded snapshot (implies --offline)
//   --record <dir>     fetch a live snapshot and save it there (as a new fixture), then render it
//   --tz <id>          time zone of the time axis (default: local for live data, UTC offline)
// Golden images: dotnet run --project tools/RenderMockup -- --offline --out tests/Chart.Render.Tests/Golden
const string Usage = "usage: RenderMockup [--out <dir>] [--offline | --fixtures <dir> | --record <dir>] [--tz <id>]";

string outDir = "mockups";
string? fixtures = null, record = null, tz = null;
var offline = false;
for (var i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value. {Usage}");
    switch (args[i])
    {
        case "--out": outDir = Value(); break;
        case "--offline": offline = true; break;
        case "--fixtures": fixtures = Value(); offline = true; break;
        case "--record": record = Value(); break;
        case "--tz": tz = Value(); break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default: Console.Error.WriteLine($"unknown argument {args[i]}. {Usage}"); return 2;
    }
}

Snapshot snapshot;
if (offline)
{
    fixtures ??= Snapshot.FixtureDirectory(RepositoryRoot());
    snapshot = Snapshot.Load(fixtures);
    Console.WriteLine($"snapshot: {fixtures} (taken {snapshot.TakenAt:u})");
}
else
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    snapshot = await Snapshot.FetchAsync(timeout.Token);
    Console.WriteLine($"snapshot: live Binance REST at {snapshot.TakenAt:u}");
    if (record is not null)
    {
        snapshot.Save(record);
        Console.WriteLine($"recorded: {Path.GetFullPath(record)}");
    }
}

var zone = tz is not null ? TimeZoneInfo.FindSystemTimeZoneById(tz) : offline ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
using var renderer = new HeadlessRenderer();
foreach (var (screen, frame) in snapshot.Frames(zone))
{
    var image = renderer.Render(frame);
    var path = Path.GetFullPath(Path.Combine(outDir, screen.FileName));
    renderer.SavePng(image, path);
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{path}  {image.Width}x{image.Height} @ {screen.DpiScale:0.##}  {frame.Layout.Profile}, {frame.Layout.DrawnCandleCount} candles, time axis {zone.Id}"));
}

return 0;

static string RepositoryRoot()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BitcoinWallpaper.sln")))
            {
                return dir.FullName;
            }
        }
    }

    throw new DirectoryNotFoundException("BitcoinWallpaper.sln not found above the current or the program's directory; pass --fixtures <dir>.");
}
