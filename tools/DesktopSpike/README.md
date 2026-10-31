# DesktopSpike (task #5, throwaway)

Proves that we can draw behind the desktop icons on Windows 11 24H2+ ("raised desktop"): a deep-blue
rectangle with a slowly moving white bar on the monitor under the mouse cursor, presented through a
DirectComposition flip-model swap chain (D3D11 + Direct2D via Vortice.Windows, MIT).

Run from the repository root (PowerShell):

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project tools/DesktopSpike -c Release -- --seconds 20
```

Stop it with Ctrl+C, by closing the console, or let `--seconds N` run out. Everything is logged to the
console and to `%TEMP%\DesktopSpike.log`.

| Option | Meaning |
|---|---|
| `--seconds N` | stop by itself after N seconds |
| `--layout auto\|legacy\|raised` | force the attach path instead of detecting it |
| `--plain-child` | raised layout: plain child window instead of a `WS_EX_LAYERED` one |
| `--dry-run` | detect and log the layout only; attaches nothing |
| `--windowed` | draw in an ordinary window; checks the drawing path without touching the desktop |

## Technique

Written from the Win32 documentation and `coordinator/research/stack.md`; no code taken from GPL projects.

- **Detection** (`DesktopLayout.cs`, unit-tested): Progman with `WS_EX_NOREDIRECTIONBITMAP` → raised,
  otherwise legacy. Where `SHELLDLL_DefView` lives and whether it is layered only produce warnings.
- **Both layouts**: message `0x052C` to Progman makes the shell create its wallpaper WorkerW.
- **Legacy**: our window becomes a child of the first top-level WorkerW after the window hosting DefView.
- **Raised**: our window becomes a `WS_EX_LAYERED | WS_EX_TRANSPARENT` child of Progman (alpha 255),
  directly below DefView; the shell's WorkerW child goes to the bottom. A WinEvent hook on the WorkerW's
  destruction re-attaches.
- **Exit**: our window is destroyed and the shell's window is asked to repaint. No
  `SPI_SETDESKWALLPAPER` (on the raised layout it destroys the WorkerW).
- **DPI**: PerMonitorV2 manifest; the awareness of the thread, our window and the parent is logged
  after attaching.
