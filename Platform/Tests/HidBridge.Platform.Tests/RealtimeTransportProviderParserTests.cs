using HidBridge.Abstractions;
using HidBridge.Application;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies supported aliases for realtime transport provider parsing.
/// </summary>
public sealed class RealtimeTransportProviderParserTests
{
    /// <summary>
    /// Accepts canonical enum token emitted by server-side Open/Ensure flows.
    /// </summary>
    [Fact]
    public void TryParse_ReturnsWebRtc_WhenValueIsEnumToken()
    {
        var parsed = RealtimeTransportProviderParser.TryParse("WebRtcDataChannel", out var provider);

        Assert.True(parsed);
        Assert.Equal(RealtimeTransportProvider.WebRtcDataChannel, provider);
    }

    /// <summary>
    /// Rejects unknown transport provider values.
    /// </summary>
    [Fact]
    public void TryParse_ReturnsFalse_WhenValueIsUnknown()
    {
        var parsed = RealtimeTransportProviderParser.TryParse("webrtc-v2", out var provider);

        Assert.False(parsed);
        Assert.Equal(RealtimeTransportProvider.Uart, provider);
    }
}
