# Test fixtures (#3)

Recorded 2026-10-31 02:05:15 UTC from the public endpoints (no key):

| file | source |
|---|---|
| `binance/rest_klines_5m.json` | `https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=5` |
| `binance/rest_ticker_24hr.json` | `https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCUSDT` |
| `kraken/rest_ohlc_5m.json` | `https://api.kraken.com/0/public/OHLC?pair=XBTUSD&interval=5` (721 rows) |
| `kraken/rest_ticker.json` | `https://api.kraken.com/0/public/Ticker?pair=XBTUSD` |

The WebSocket messages (`ws_*.json`) are written by hand in the official payload formats (Binance raw
stream `<symbol>@kline_5m` and `<symbol>@miniTicker`; Kraken WS v2 channel `ohlc`), continuing the
recorded REST data: the forming 02:05 candle is updated, closed (`x: true`), and the 02:10 candle starts.
