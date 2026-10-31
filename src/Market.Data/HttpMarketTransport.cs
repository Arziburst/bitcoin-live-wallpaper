using System.Net.WebSockets;
using System.Text;

namespace BitcoinWallpaper.Market.Data;

/// <summary>The production network: HttpClient for REST, ClientWebSocket for streams. No API key.</summary>
public sealed class HttpMarketTransport : IMarketTransport, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient http;

    public HttpMarketTransport()
    {
        http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = RequestTimeout,
        })
        {
            Timeout = RequestTimeout,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BitcoinWallpaper/0.1");
    }

    public async Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        return new HttpResult((int)response.StatusCode, body, retryAfter);
    }

    public async Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        // Client pings keep idle connections (Kraken closes them after about a minute) and NAT entries alive;
        // server pings (Binance, every 20 s) are answered by the runtime.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
            return new Socket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => http.Dispose();

    private sealed class Socket(ClientWebSocket socket) : IMarketSocket
    {
        private readonly byte[] buffer = new byte[16 * 1024];

        public async Task SendAsync(string text, CancellationToken cancellationToken) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text
                        ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)
                        : string.Empty;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(CloseTimeout);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The peer is gone; disposing below aborts the connection.
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
