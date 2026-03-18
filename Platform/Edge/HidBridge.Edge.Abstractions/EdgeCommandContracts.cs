namespace HidBridge.Edge.Abstractions;

/// <summary>
/// Describes one control command that the edge runtime must execute against a target device path.
/// </summary>
/// <param name="CommandId">Stable command identifier used for ACK correlation.</param>
/// <param name="Action">Semantic action name such as keyboard or mouse operation.</param>
/// <param name="Args">Action-specific command payload arguments.</param>
/// <param name="TimeoutMs">Maximum command execution timeout in milliseconds.</param>
public sealed record EdgeCommandRequest(
    string CommandId,
    string Action,
    IReadOnlyDictionary<string, object?> Args,
    int TimeoutMs);

/// <summary>
/// Represents one edge-side command execution result before it is mapped to platform ACK payload.
/// </summary>
/// <param name="IsSuccess">True when command completed successfully on device path.</param>
/// <param name="IsTimeout">True when command timed out on edge path.</param>
/// <param name="ErrorCode">Optional structured error code.</param>
/// <param name="ErrorMessage">Optional human-readable error details.</param>
/// <param name="RoundtripMs">Observed control roundtrip in milliseconds.</param>
public sealed record EdgeCommandExecutionResult(
    bool IsSuccess,
    bool IsTimeout,
    string? ErrorCode,
    string? ErrorMessage,
    double RoundtripMs)
{
    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static EdgeCommandExecutionResult Applied(double roundtripMs)
        => new(true, false, null, null, roundtripMs);

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    public static EdgeCommandExecutionResult Rejected(string errorCode, string errorMessage, double roundtripMs)
        => new(false, false, errorCode, errorMessage, roundtripMs);

    /// <summary>
    /// Creates a timeout result.
    /// </summary>
    public static EdgeCommandExecutionResult Timeout(string errorCode, string errorMessage, double roundtripMs)
        => new(false, true, errorCode, errorMessage, roundtripMs);
}

/// <summary>
/// Executes one edge command against device-facing transport.
/// </summary>
public interface IEdgeCommandExecutor
{
    /// <summary>
    /// Executes command and returns normalized edge-side execution result.
    /// </summary>
    Task<EdgeCommandExecutionResult> ExecuteAsync(EdgeCommandRequest request, CancellationToken cancellationToken);
}
