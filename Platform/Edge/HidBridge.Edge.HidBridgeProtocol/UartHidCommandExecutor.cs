using System.Diagnostics;
using HidBridge.Edge.Abstractions;
using HidBridge.Transport.Uart;
using Microsoft.Extensions.Logging;

namespace HidBridge.Edge.HidBridgeProtocol;

/// <summary>
/// Executes edge commands directly against HID bridge UART transport.
/// </summary>
public sealed class UartHidCommandExecutor : IEdgeCommandExecutor, IAsyncDisposable
{
    private readonly UartHidCommandExecutorOptions _options;
    private readonly ILogger<UartHidCommandExecutor> _logger;
    private readonly SemaphoreSlim _executeLock = new(1, 1);
    private HidBridgeUartClient? _client;

    /// <summary>
    /// Initializes a UART-backed command executor.
    /// </summary>
    public UartHidCommandExecutor(UartHidCommandExecutorOptions options, ILogger<UartHidCommandExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PortName))
        {
            throw new ArgumentException("UART port name is required.", nameof(options));
        }

        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes one edge command over UART and returns normalized execution result.
    /// </summary>
    public async Task<EdgeCommandExecutionResult> ExecuteAsync(EdgeCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Max(1000, request.TimeoutMs));

        await _executeLock.WaitAsync(timeoutCts.Token);
        try
        {
            var client = await GetClientLockedAsync(timeoutCts.Token);
            await ExecuteCoreAsync(client, request, timeoutCts.Token);
            return EdgeCommandExecutionResult.Applied(stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EdgeCommandExecutionResult.Timeout(
                errorCode: "E_TRANSPORT_TIMEOUT",
                errorMessage: $"UART peer timeout for action '{request.Action}'.",
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (HidBridgeUartDeviceException ex)
        {
            var info = ex.ToErrorInfo();
            return EdgeCommandExecutionResult.Rejected(
                errorCode: string.IsNullOrWhiteSpace(info.Code) ? "E_COMMAND_EXECUTION_FAILED" : info.Code,
                errorMessage: info.Message,
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UART command execution failed for action {Action}.", request.Action);
            return EdgeCommandExecutionResult.Rejected(
                errorCode: "E_COMMAND_EXECUTION_FAILED",
                errorMessage: ex.Message,
                roundtripMs: stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            try
            {
                if (_options.ReleasePortAfterExecute)
                {
                    await ReleaseClientLockedAsync();
                }
            }
            finally
            {
                _executeLock.Release();
            }
        }
    }

    /// <summary>
    /// Releases UART resources when the runtime is stopping.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _executeLock.WaitAsync();
        try
        {
            await ReleaseClientLockedAsync();
        }
        finally
        {
            _executeLock.Release();
            _executeLock.Dispose();
        }
    }

    /// <summary>
    /// Creates UART client on first command and reuses it while executor is alive.
    /// </summary>
    private async Task<HidBridgeUartClient> GetClientLockedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        _client = new HidBridgeUartClient(new HidBridgeUartClientOptions(
            PortName: _options.PortName,
            BaudRate: _options.BaudRate,
            HmacKey: _options.HmacKey,
            MasterSecret: _options.MasterSecret,
            MouseInterfaceSelector: _options.MouseInterfaceSelector,
            KeyboardInterfaceSelector: _options.KeyboardInterfaceSelector,
            CommandTimeoutMs: _options.CommandTimeoutMs,
            InjectTimeoutMs: _options.InjectTimeoutMs,
            InjectRetries: _options.InjectRetries));

        if (!string.IsNullOrWhiteSpace(_options.MasterSecret))
        {
            try
            {
                await _client.EnsureDerivedKeyAsync(_options.MasterSecret, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize UART derived key. Continuing with bootstrap key.");
            }
        }

        return _client;
    }

    /// <summary>
    /// Disposes current UART client instance and clears cached reference.
    /// </summary>
    private async Task ReleaseClientLockedAsync()
    {
        if (_client is null)
        {
            return;
        }

        var instance = _client;
        _client = null;
        await instance.DisposeAsync();
    }

    /// <summary>
    /// Maps semantic edge action names to concrete UART HID operations.
    /// </summary>
    private static async Task ExecuteCoreAsync(HidBridgeUartClient client, EdgeCommandRequest request, CancellationToken cancellationToken)
    {
        await HidBridgeUartCommandDispatcher.ExecuteAsync(
            client,
            request.Action,
            request.Args,
            cancellationToken);
    }
}
