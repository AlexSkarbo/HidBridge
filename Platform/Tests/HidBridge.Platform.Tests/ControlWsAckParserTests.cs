using System.Text.Json;
using HidBridge.Edge.HidBridgeProtocol;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies ACK parsing compatibility for exp-022 style websocket payloads.
/// </summary>
public sealed class ControlWsAckParserTests
{
    [Fact]
    public void Parse_ReturnsSuccessForNestedStatusApplied()
    {
        using var document = JsonDocument.Parse("""
            {
              "payload": {
                "id": "cmd-1",
                "status": "applied"
              }
            }
            """);

        var result = ControlWsAckParser.Parse(document.RootElement, "cmd-1");

        Assert.True(result.Found);
        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Fact]
    public void Parse_ReturnsRejectedWhenErrorTextExists()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "cmd-1",
              "ok": false,
              "error": "device busy"
            }
            """);

        var result = ControlWsAckParser.Parse(document.RootElement, "cmd-1");

        Assert.True(result.Found);
        Assert.False(result.IsSuccess);
        Assert.Equal("device busy", result.ErrorMessage);
    }

    [Fact]
    public void Parse_IgnoresMismatchedCommandId()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "cmd-other",
              "ok": true
            }
            """);

        var result = ControlWsAckParser.Parse(document.RootElement, "cmd-1");

        Assert.False(result.Found);
        Assert.Null(result.IsSuccess);
    }
}
