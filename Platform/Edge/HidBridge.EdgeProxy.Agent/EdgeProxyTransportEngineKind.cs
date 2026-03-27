namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Selects control transport engine used by edge proxy worker.
/// </summary>
public enum EdgeProxyTransportEngineKind
{
    /// <summary>
    /// Relay queue polling engine kept as compatibility fallback path.
    /// </summary>
    RelayCompat,

    /// <summary>
    /// DataChannelDotNet control engine (production path).
    /// </summary>
    DataChannelDotNet,
}
