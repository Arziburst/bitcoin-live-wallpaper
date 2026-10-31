# Decisions

Owner's decisions. Each entry: id, date, decision.

- **D-1** (2026-10-31) The product is a live desktop wallpaper behind the desktop icons, not a screensaver.
- **D-2** (2026-10-31) When a fullscreen application is in front, the wallpaper pauses.
- **D-3** (2026-10-31) Desktop PC, no battery mode. One monitor is active at a time: Samsung C49RG9x 5120x1440 @120 Hz (32:9, 175 % scaling) for work, a Xiaomi 4K TV 3840x2160 (16:9) for games and series; the wallpaper adapts automatically when the active monitor changes.
- **D-4** (2026-10-31) Market data from the most reliable open source: Binance BTC/USDT primary, Kraken BTC/USD fallback; beauty matters more than exact numbers.
- **D-5** (2026-10-31) On screen: only the chart, on a dark background that is not pure black; maximally informative (candles, numbers, the usual candlestick-chart features); live updates at a sensible cadence.
- **D-6** (2026-10-31) No felt performance impact; must run on current Windows 11 without glitches.
- **D-7** (2026-10-31) Stack: native C#/.NET 10, Direct2D/DirectWrite on a DirectComposition flip-model swap chain; no browser engine.
- **D-8** (2026-10-31) 5-minute candles.
- **D-9** (2026-10-31) Before the live wallpaper is built, the chart is reviewed as static PNG mockups at 5120x1440 and 3840x2160.
- **D-10** (2026-10-31) Every change on the chart (the forming candle, the new-bar shift, the price change) animates smoothly at the monitor's refresh rate (120 Hz on the desk monitor); nothing is drawn while nothing moves.
- **D-11** (2026-10-31) A 24h high or low outside the visible price range is shown as an edge marker on the price scale; the scale is not stretched for it.
- **D-12** (2026-10-31) No watermark on the chart; the mockup look of PR #14 is accepted.
- **D-13** (2026-10-31) The wallpaper shows the last 30 days of history; the candle interval is chosen per screen so that 30 days fit (4-hour candles on the 5120x1440 desk monitor, 6-hour on the 3840x2160 TV); replaces D-8.
- **D-20** (2026-10-31) The content spans the full screen width: no safe area is reserved for desktop icons.
- **D-29** (2026-10-31) The chart fills all drawable candle slots with older history beyond the 30 days (desk ≈ 48 days at 4h); the candle pitch is unchanged. On 16:9 the headline sits at the left margin.
- **D-30** (2026-10-31) The chart's left edge runs off the left screen edge; on the right the candles and lines run up to the price axis (no empty gap), and the price axis keeps a small margin from the right screen edge.
- **D-15** (2026-10-31) When the display is off, the wallpaper does not update the picture.
- **D-24** (2026-10-31) If the wallpaper process crashes, it restarts by itself - at most 3 times in 10 minutes (then stays down and logs it), and the crash reason (exception, stack) goes to the log.
- **D-26** (2026-10-31) The tray tooltip shows the state - e.g. "Live · Binance · 77 182" / "offline since 14:20" / "Paused (fullscreen)" / "Off"; tray items "Open logs" (opens %LOCALAPPDATA%\BitcoinWallpaper\logs) and "Version x.y.z".
- **D-32** (2026-10-31) The price-axis block (price labels and price tags) is centred between the newest candle and the right screen edge; this replaces D-30's "small margin from the right screen edge".
- **D-33** (2026-10-31) The installer file is named BLW_Installer.exe.
- **D-21** (2026-10-31) A screensaver mode - the same live chart full-screen on top of everything (no desktop icons), closing on mouse/keyboard input like any screensaver; a tray check mark "Use as screensaver": on sets this app as the Windows screensaver for the current user (the timeout stays the Windows setting), off restores the previous screensaver; uninstall restores it too.
- **D-34** (2026-10-31) The lock screen shows a snapshot of the chart, refreshed every 30 minutes and at once on a new candle; a tray item turns it off and restores the user's own picture.
