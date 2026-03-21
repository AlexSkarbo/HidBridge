namespace HidBridge.Transport.Uart;

/// <summary>
/// Provides deterministic retry orchestration for UART command attempts.
/// </summary>
internal static class UartCommandRetryExecutor
{
    /// <summary>
    /// Executes command attempts until one succeeds (non-null) or retry budget is exhausted.
    /// </summary>
    /// <typeparam name="TResult">Reference result type returned by one attempt.</typeparam>
    /// <param name="retries">Retry count after initial attempt.</param>
    /// <param name="attemptAsync">Attempt callback receiving zero-based attempt number.</param>
    /// <param name="cancellationToken">Cancels retry loop.</param>
    /// <returns>First non-null result, or <c>null</c> when all attempts return null.</returns>
    internal static async Task<TResult?> ExecuteAsync<TResult>(
        int retries,
        Func<int, Task<TResult?>> attemptAsync,
        CancellationToken cancellationToken)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(attemptAsync);

        var totalAttempts = Math.Max(1, retries + 1);
        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await attemptAsync(attempt).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
