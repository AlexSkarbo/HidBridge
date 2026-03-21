namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Selects control transport engine used by edge proxy worker.
/// </summary>
public enum EdgeProxyTransportEngineKind
{
    /// <summary>
    /// Existing relay queue polling engine (default, production path).
    /// </summary>
    RelayCompat,

    /// <summary>
    /// Preview DataChannelDotNet-oriented control engine (currently relay-compatible).
    /// </summary>
    DataChannelDotNet,
}
