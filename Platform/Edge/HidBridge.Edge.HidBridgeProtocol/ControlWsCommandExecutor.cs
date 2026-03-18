using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HidBridge.Edge.Abstractions;
using Microsoft.Extensions.Logging;

namespace HidBridge.Edge.HidBridgeProtocol;

/// <summary>
/// Executes edge control commands through exp-022 compatible control websocket endpoint.
/// </summary>
public sealed class ControlWsCommandExecutor : IEdgeCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Uri _controlWsUri;
    private readonly ILogger<ControlWsCommandExecutor> _logger;

    /// <summary>
    /// Initializes a new websocket-backed command executor.
    /// </summary>
    public ControlWsCommandExecutor(string controlWsUrl, ILogger<ControlWsCommandExecutor> logger)
    {
        if (!Uri.TryCreate(controlWsUrl, UriKind.Absolute, out var parsedUri))
        {
            throw new ArgumentException("Control websocket URL must be absolute.", nameof(controlWsUrl));
        }

        _controlWsUri = parsedUri;
        _logger = logger;
    }

    /// <summary>
    /// Executes one command and returns normalized result with roundtrip metrics.
    /// </summary>
    public async Task<EdgeCommandExecutionResult> ExecuteAsync(EdgeCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var socket = new ClientWebSocket();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Math.Max(1000, request.TimeoutMs));

            await socket.ConnectAsync(_controlWsUri, timeoutCts.Token);

            var payload = BuildPayload(request);
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            await socket.SendAsync(payloadBytes, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token);

            var receiveBuffer = new byte[16 * 1024];
            var expectedCommandId = request.CommandId;
            while (!timeoutCts.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await socket.ReceiveAsync(receiveBuffer, timeoutCts.Token);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        return EdgeCommandExecutionResult.Rejected(
                            errorCode: "E_WEBRTC_PEER_EXECUTION_FAILED",
                            errorMessage: "Control websocket closed before ACK.",
                            roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
                    }

                    stream.Write(receiveBuffer, 0, receiveResult.Count);
                }
                while (!receiveResult.EndOfMessage);

                var raw = Encoding.UTF8.GetString(stream.ToArray());
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(raw);
                var ack = ControlWsAckParser.Parse(document.RootElement, expectedCommandId);
                if (!ack.Found)
                {
                    continue;
                }

                if (ack.IsSuccess == true)
                {
                    await CloseSocketQuietlyAsync(socket);
                    return EdgeCommandExecutionResult.Applied(stopwatch.Elapsed.TotalMilliseconds);
                }

                var rejectionMessage = string.IsNullOrWhiteSpace(ack.ErrorMessage)
                    ? "Peer returned non-success response."
                    : ack.ErrorMessage;

                await CloseSocketQuietlyAsync(socket);
                return EdgeCommandExecutionResult.Rejected(
                    errorCode: "E_WEBRTC_PEER_EXECUTION_FAILED",
                    errorMessage: rejectionMessage,
                    roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
            }

            return EdgeCommandExecutionResult.Timeout(
                errorCode: "E_TRANSPORT_TIMEOUT",
                errorMessage: $"WebRTC peer timeout for action '{request.Action}'.",
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EdgeCommandExecutionResult.Timeout(
                errorCode: "E_TRANSPORT_TIMEOUT",
                errorMessage: $"WebRTC peer timeout for action '{request.Action}'.",
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control websocket execution failed for action {Action}.", request.Action);
            return EdgeCommandExecutionResult.Rejected(
                errorCode: "E_WEBRTC_PEER_ADAPTER_FAILURE",
                errorMessage: ex.Message,
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Builds command payload shape expected by exp-022 compatible websocket control endpoint.
    /// </summary>
    private static Dictionary<string, object?> BuildPayload(EdgeCommandRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = request.CommandId,
            ["type"] = request.Action,
        };

        foreach (var pair in request.Args)
        {
            if (!payload.ContainsKey(pair.Key))
            {
                payload[pair.Key] = pair.Value;
            }
        }

        return payload;
    }

    /// <summary>
    /// Attempts graceful websocket close to reduce noisy remote close-handshake warnings.
    /// </summary>
    private static async Task CloseSocketQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "command-ack-complete", closeCts.Token);
        }
        catch
        {
            socket.Abort();
        }
    }
}
