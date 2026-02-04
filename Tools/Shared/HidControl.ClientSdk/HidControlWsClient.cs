using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HidControl.ClientSdk;

/// <summary>
/// WebSocket client with basic reconnect support for HID streaming endpoints.
/// </summary>
public sealed class HidControlWsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Sets a request header to be used on the next Connect call.
    /// Must be called before connecting.
    /// </summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    public void SetRequestHeader(string name, string value)
        => _ws.Options.SetRequestHeader(name, value);

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
    /// Sends a text message (UTF-8).
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a text message.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task SendTextAsync(string text, CancellationToken ct = default)
        => SendAsync(Encoding.UTF8.GetBytes(text), ct);

    /// <summary>
    /// Sends a JSON message.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="json">Serializer options (optional).</param>
    /// <param name="ct">The cancellation token.</param>
    public Task SendJsonAsync<T>(T payload, JsonSerializerOptions? json = null, CancellationToken ct = default)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload, json);
        return SendAsync(data, ct);
    }

    /// <summary>
    /// Sends a binary message (for endpoints that accept binary frames).
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
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
    /// Receives a single complete message as UTF-8 text.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Text payload, or null if the socket was closed.</returns>
    public async Task<string?> ReceiveTextOnceAsync(CancellationToken ct = default)
    {
        byte[] buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Closes the WebSocket connection.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The server may close/abort the connection without a proper close handshake.
                // Treat this as "already closed" for our wrapper use cases.
            }
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
