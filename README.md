# BitcoinWallpaper

A live BTC/USDT candlestick chart as the Windows 11 desktop wallpaper: it is drawn behind the desktop
icons, updates live (5-minute candles), pauses while a fullscreen application is in front and adapts
when the active monitor changes. Native C#/.NET 10 with Direct2D/DirectWrite on a DirectComposition
flip-model swap chain; no browser engine. The owner's decisions are in [docs/decisions.md](docs/decisions.md).

Status: solution skeleton only — the projects are stubs that later tasks fill.

## Development

### .NET 10 SDK (per user, no admin)

The SDK version is pinned in [global.json](global.json) (`rollForward: latestFeature`). Install it for
the current user only, without admin rights and without touching the machine-wide `PATH`:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0
```

It installs into `%LOCALAPPDATA%\Microsoft\dotnet`. Use it in a shell session (PowerShell):

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version    # prints 10.0.x
```

or call it by full path: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" --version`.

### Build and test

```powershell
dotnet build BitcoinWallpaper.sln -c Release
dotnet test BitcoinWallpaper.sln -c Release
```

Warnings are errors and nullable reference types are on for every project
([Directory.Build.props](Directory.Build.props)). CI ([.github/workflows/ci.yml](.github/workflows/ci.yml))
runs restore, build and test on `windows-latest` for every pull request and every push to `main`.

## Repository layout

| Path | Target | Purpose |
|---|---|---|
| `src/Chart.Core` | `net10.0` | Candle model, layout engine, pause decision. Pure, no Windows dependencies. |
| `src/Market.Data` | `net10.0` | Market data client: Binance BTC/USDT primary, Kraken BTC/USD fallback. |
| `src/Chart.Render.D2D` | `net10.0-windows` | Direct2D/DirectWrite renderer. |
| `src/Wallpaper.Host` | `net10.0-windows` | The wallpaper process (WinExe, PerMonitorV2 DPI manifest). |
| `tests/Chart.Core.Tests` | `net10.0` | xUnit tests for Chart.Core. |
| `tests/Market.Data.Tests` | `net10.0` | xUnit tests for Market.Data. |
| `docs/decisions.md` | | Decision log (D-n entries). |
