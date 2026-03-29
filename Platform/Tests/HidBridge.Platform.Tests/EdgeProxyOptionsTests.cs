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
    public void Normalize_DcdAllowRelayFallback_DefaultsFalse()
    {
        var options = CreateBaselineOptions();

        options.Normalize();

        Assert.False(options.DcdAllowRelayFallback);
    }

    [Fact]
    public void IsValid_DefaultSwitchMode_UsesDefaultSwitch()
    {
        var options = CreateBaselineOptions();
        options.EngineSwitchMode = string.Empty;
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyEngineSwitchMode.DefaultSwitch, options.GetEngineSwitchMode());
    }

    [Fact]
    public void IsValid_RejectsUnknownSwitchMode()
    {
        var options = CreateBaselineOptions();
        options.EngineSwitchMode = "unsupported";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("EngineSwitchMode", error);
    }

    [Fact]
    public void Normalize_ClampsSwitchSloThresholds()
    {
        var options = CreateBaselineOptions();
        options.EngineCanaryPercent = 180;
        options.EngineSloMinCommandSampleCount = 0;
        options.EngineSloAckTimeoutRateMaxPct = -10;
        options.EngineSloReconnectFrequencyMaxPerHour = -5;
        options.EngineSloRoundtripP95MsMax = 50;

        options.Normalize();

        Assert.Equal(100, options.EngineCanaryPercent);
        Assert.Equal(1, options.EngineSloMinCommandSampleCount);
        Assert.Equal(0.0, options.EngineSloAckTimeoutRateMaxPct);
        Assert.Equal(0.0, options.EngineSloReconnectFrequencyMaxPerHour);
        Assert.Equal(100, options.EngineSloRoundtripP95MsMax);
    }

    [Fact]
    public void IsValid_DefaultMediaEngine_UsesNone()
    {
        var options = CreateBaselineOptions();
        options.MediaEngine = string.Empty;
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyMediaEngineKind.None, options.GetMediaEngineKind());
    }

    [Fact]
    public void IsValid_FfmpegMediaEngine_IsAcceptedAsPreviewMode()
    {
        var options = CreateBaselineOptions();
        options.MediaEngine = "ffmpeg-dcd";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyMediaEngineKind.FfmpegDataChannelDotNet, options.GetMediaEngineKind());
    }

    [Fact]
    public void IsValid_FfmpegLatencyProfile_IsParsedFromAliases()
    {
        var options = CreateBaselineOptions();
        options.FfmpegLatencyProfile = "ultra-low-latency";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
        Assert.Equal(EdgeProxyFfmpegLatencyProfile.Ultra, options.GetFfmpegLatencyProfile());
    }

    [Fact]
    public void IsValid_RejectsUnknownFfmpegLatencyProfile()
    {
        var options = CreateBaselineOptions();
        options.FfmpegLatencyProfile = "turbo";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("FfmpegLatencyProfile", error);
    }

    [Fact]
    public void IsValid_RejectsUnknownMediaEngine()
    {
        var options = CreateBaselineOptions();
        options.MediaEngine = "unsupported-media";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MediaEngine", error);
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
    public void IsValid_RejectsInvalidMediaWhipUrl()
    {
        var options = CreateBaselineOptions();
        options.MediaWhipUrl = "not-a-url";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MediaWhipUrl", error);
    }

    [Fact]
    public void IsValid_RejectsInvalidMediaWhepUrl()
    {
        var options = CreateBaselineOptions();
        options.MediaWhepUrl = "not-a-url";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MediaWhepUrl", error);
    }

    [Fact]
    public void IsValid_MediaBackendAutoStart_RequiresExecutablePath()
    {
        var options = CreateBaselineOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = string.Empty;
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MediaBackendExecutablePath", error);
    }

    [Fact]
    public void IsValid_MediaBackendAutoStart_WithExecutablePath_IsAccepted()
    {
        var options = CreateBaselineOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = "srs";
        options.Normalize();

        var isValid = options.IsValid(out var error);

        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
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
