using HidBridge.Transport.Uart;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies deterministic retry/timeout orchestration semantics used by UART command flow.
/// </summary>
public sealed class UartRetryTimeoutTests
{
    /// <summary>
    /// Ensures timeout-like null attempt results exhaust retry budget and return null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AllAttemptsTimeout_ReturnsNullAfterRetryBudget()
    {
        var attempts = 0;
        var result = await UartCommandRetryExecutor.ExecuteAsync<object>(
            retries: 2,
            attemptAsync: _ =>
            {
                attempts++;
                return Task.FromResult<object?>(null);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(3, attempts);
    }

    /// <summary>
    /// Ensures retry loop stops immediately once one attempt returns a non-null result.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AttemptSucceeds_StopsFurtherRetries()
    {
        var attempts = 0;
        var expected = new object();
        var result = await UartCommandRetryExecutor.ExecuteAsync(
            retries: 5,
            attemptAsync: _ =>
            {
                attempts++;
                return Task.FromResult<object?>(attempts == 2 ? expected : null);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Ensures command-level device exceptions are not swallowed as retryable null outcomes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AttemptThrowsDeviceError_DoesNotRetry()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HidBridgeUartDeviceException>(() =>
            UartCommandRetryExecutor.ExecuteAsync<object>(
                retries: 3,
                attemptAsync: _ =>
                {
                    attempts++;
                    throw new HidBridgeUartDeviceException(0x04);
                },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
    }
}
