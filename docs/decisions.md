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
