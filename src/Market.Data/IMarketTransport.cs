namespace BitcoinWallpaper.Market.Data;

/// <summary>An HTTP answer of any status. RetryAfter is the Retry-After header as a delay, if present.</summary>
public sealed record HttpResult(int StatusCode, string Body, TimeSpan? RetryAfter = null);

/// <summary>Network seam. Network failures throw (HttpRequestException, WebSocketException, IOException);
/// any HTTP status is returned as an HttpResult.</summary>
public interface IMarketTransport
{
    Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken);

    Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A connected WebSocket carrying whole text messages.</summary>
public interface IMarketSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next whole text message; null when the server closed the socket.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}
