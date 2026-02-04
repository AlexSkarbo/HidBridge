namespace HidControl.ClientSdk;

/// <summary>
/// Simple retry policy with exponential backoff.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Executes an operation with retry/backoff.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="operation">Operation to execute.</param>
    /// <param name="maxAttempts">Maximum attempts (must be >= 1).</param>
    /// <param name="baseDelayMs">Initial delay in milliseconds.</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Result.</returns>
    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, int maxAttempts = 3, int baseDelayMs = 200, int maxDelayMs = 2000, CancellationToken ct = default)
    {
        if (maxAttempts < 1) maxAttempts = 1;
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                int delay = ComputeDelay(baseDelayMs, maxDelayMs, attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Computes the backoff delay for the given attempt.
    /// </summary>
    /// <param name="baseDelayMs">Initial delay in milliseconds.</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds.</param>
    /// <param name="attempt">Attempt number (1-based).</param>
    /// <returns>Delay in milliseconds.</returns>
    public static int ComputeDelay(int baseDelayMs, int maxDelayMs, int attempt)
    {
        if (baseDelayMs < 0) baseDelayMs = 0;
        if (maxDelayMs < baseDelayMs) maxDelayMs = baseDelayMs;
        double factor = Math.Pow(2, Math.Max(0, attempt - 1));
        double raw = baseDelayMs * factor;
        if (raw > maxDelayMs) raw = maxDelayMs;
        return (int)raw;
    }
}
