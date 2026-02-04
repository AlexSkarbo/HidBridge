using System.Net.WebSockets;

namespace HidControl.ClientSdk;

/// <summary>
/// WebSocket client with basic reconnect support for HID streaming endpoints.
/// </summary>
public sealed class HidControlWsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Connects to the WebSocket endpoint.
    /// </summary>
    /// <param name="uri">The WebSocket URI.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        => _ws.ConnectAsync(uri, ct);

    /// <summary>
    /// Connects with retry/backoff until success or cancellation.
    /// </summary>
    /// <param name="uri">The WebSocket URI.</param>
    /// <param name="maxAttempts">Maximum attempts (0 = infinite).</param>
    /// <param name="baseDelayMs">Initial delay between attempts.</param>
    /// <param name="maxDelayMs">Maximum delay between attempts.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task ConnectWithRetryAsync(Uri uri, int maxAttempts = 0, int baseDelayMs = 200, int maxDelayMs = 5000, CancellationToken ct = default)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await ConnectAsync(uri, ct).ConfigureAwait(false);
                return;
            }
            catch when (!ct.IsCancellationRequested)
            {
                if (maxAttempts > 0 && attempt >= maxAttempts)
                {
                    throw;
                }
                int delay = RetryPolicy.ComputeDelay(baseDelayMs, maxDelayMs, attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Sends a binary message.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Runs a receive loop and dispatches complete messages to the handler.
    /// </summary>
    /// <param name="onMessage">Message handler.</param>
    /// <param name="onDisconnect">Disconnect handler.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task RunReceiveLoopAsync(Func<ReadOnlyMemory<byte>, ValueTask> onMessage, Func<Exception?, ValueTask>? onDisconnect = null, CancellationToken ct = default)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                await onMessage(ms.ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (onDisconnect is not null)
            {
                await onDisconnect(ex).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (onDisconnect is not null && _ws.State != WebSocketState.Open)
            {
                await onDisconnect(null).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Closes the WebSocket connection.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_ws.State == WebSocketState.Open)
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the WebSocket and send lock.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _ws.Dispose();
        _sendLock.Dispose();
    }
}
