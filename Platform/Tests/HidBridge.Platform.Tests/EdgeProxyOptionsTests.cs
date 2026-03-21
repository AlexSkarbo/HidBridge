using HidBridge.EdgeProxy.Agent;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Validates command-executor option parsing and mode-specific requirements.
/// </summary>
public sealed class EdgeProxyOptionsTests
{
    [Fact]
    public void IsValid_ControlWsMode_RequiresAbsoluteControlWsUrl()
    {
        var options = CreateBaselineOptions();
        options.CommandExecutor = "controlws";
        options.ControlWsUrl = "not-a-url";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("ControlWsUrl", error);
    }

    [Fact]
    public void IsValid_UartMode_RequiresUartPort()
    {
        var options = CreateBaselineOptions();
        options.CommandExecutor = "uart";
        options.UartPort = "";
        options.ControlWsUrl = "not-required-in-uart-mode";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("UartPort", error);
    }

    [Fact]
    public void IsValid_UartMode_DoesNotRequireControlWsUrl()
    {
        var options = CreateBaselineOptions();
        options.CommandExecutor = "uart";
        options.UartPort = "COM6";
        options.ControlWsUrl = "not-required-in-uart-mode";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyCommandExecutorKind.UartHid, options.GetCommandExecutorKind());
    }

    [Fact]
    public void IsValid_RejectsUnknownCommandExecutor()
    {
        var options = CreateBaselineOptions();
        options.CommandExecutor = "unsupported";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("CommandExecutor", error);
    }

    [Fact]
    public void IsValid_DefaultTransportEngine_UsesRelayCompat()
    {
        var options = CreateBaselineOptions();
        options.TransportEngine = string.Empty;
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyTransportEngineKind.RelayCompat, options.GetTransportEngineKind());
    }

    [Fact]
    public void IsValid_RejectsUnknownTransportEngine()
    {
        var options = CreateBaselineOptions();
        options.TransportEngine = "unsupported-engine";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("TransportEngine", error);
    }

    [Fact]
    public void IsValid_DcdTransportEngine_IsAcceptedAsPreviewMode()
    {
        var options = CreateBaselineOptions();
        options.TransportEngine = "dcd";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyTransportEngineKind.DataChannelDotNet, options.GetTransportEngineKind());
    }

    [Fact]
    public void Normalize_DcdAllowRelayFallback_DefaultsTrue()
    {
        var options = CreateBaselineOptions();

        options.Normalize();

        Assert.True(options.DcdAllowRelayFallback);
    }

    [Fact]
    public void IsValid_RejectsInvalidMediaHealthUrl()
    {
        var options = CreateBaselineOptions();
        options.MediaHealthUrl = "not-a-url";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MediaHealthUrl", error);
    }

    [Fact]
    public void Normalize_DefaultOperatorRole_UsesEdgeRole()
    {
        var options = CreateBaselineOptions();
        options.OperatorRolesCsv = string.Empty;

        options.Normalize();

        Assert.Equal("operator.edge", options.OperatorRolesCsv);
    }

    /// <summary>
    /// Creates valid baseline options for mode-specific overrides.
    /// </summary>
    private static EdgeProxyOptions CreateBaselineOptions()
    {
        return new EdgeProxyOptions
        {
            BaseUrl = "http://127.0.0.1:18093",
            SessionId = "session-1",
            PeerId = "peer-1",
            EndpointId = "endpoint-1",
            ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
            CommandExecutor = "controlws",
        };
    }
}
