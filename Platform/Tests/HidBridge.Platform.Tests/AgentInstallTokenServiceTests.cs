using HidBridge.ControlPlane.Api;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies signed install-token generation and validation behavior.
/// </summary>
public sealed class AgentInstallTokenServiceTests
{
    [Fact]
    public void CreateAndValidate_RoundTripsPayload()
    {
        var service = new AgentInstallTokenService(new AgentInstallRuntimeOptions
        {
            SigningKey = "unit-test-signing-key",
        });
        var expected = new AgentInstallBootstrapPayload(
            SessionId: "room-1",
            PeerId: "peer-1",
            EndpointId: "endpoint-1",
            BaseUrl: "http://127.0.0.1:18093",
            ControlPlaneWebUrl: "http://127.0.0.1:18110",
            UartPort: "COM6",
            UartHmacKey: "secret",
            FfmpegLatencyProfile: "balanced",
            MediaStreamId: "edge-main",
            MediaWhipUrl: "http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=edge-main",
            MediaWhepUrl: "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=edge-main",
            MediaPlaybackUrl: "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=edge-main",
            MediaHealthUrl: "http://127.0.0.1:18081/api/v1/versions",
            FfmpegExecutablePath: "ffmpeg",
            FfmpegVideoDevice: "",
            FfmpegAudioDevice: "",
            FfmpegUseTestSource: false,
            MediaBackendAutoStart: false,
            RequireMediaReady: true,
            PackageUrl: "",
            PackageSha256: "",
            AgentExecutableRelativePath: "HidBridge.EdgeProxy.Agent.exe",
            DefaultInstallDirectory: @"$env:ProgramData\HidBridge\EdgeAgent",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var token = service.CreateToken(expected);
        var ok = service.TryValidate(token, out var actual, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.EndpointId, actual.EndpointId);
        Assert.Equal(expected.MediaWhipUrl, actual.MediaWhipUrl);
    }

    [Fact]
    public void TryValidate_RejectsExpiredToken()
    {
        var service = new AgentInstallTokenService(new AgentInstallRuntimeOptions
        {
            SigningKey = "unit-test-signing-key",
        });
        var expired = new AgentInstallBootstrapPayload(
            SessionId: "room-1",
            PeerId: "peer-1",
            EndpointId: "endpoint-1",
            BaseUrl: "http://127.0.0.1:18093",
            ControlPlaneWebUrl: "http://127.0.0.1:18110",
            UartPort: "COM6",
            UartHmacKey: "secret",
            FfmpegLatencyProfile: "balanced",
            MediaStreamId: "edge-main",
            MediaWhipUrl: "http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=edge-main",
            MediaWhepUrl: "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=edge-main",
            MediaPlaybackUrl: "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=edge-main",
            MediaHealthUrl: "http://127.0.0.1:18081/api/v1/versions",
            FfmpegExecutablePath: "ffmpeg",
            FfmpegVideoDevice: "",
            FfmpegAudioDevice: "",
            FfmpegUseTestSource: false,
            MediaBackendAutoStart: false,
            RequireMediaReady: true,
            PackageUrl: "",
            PackageSha256: "",
            AgentExecutableRelativePath: "HidBridge.EdgeProxy.Agent.exe",
            DefaultInstallDirectory: @"$env:ProgramData\HidBridge\EdgeAgent",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var token = service.CreateToken(expired);
        var ok = service.TryValidate(token, out _, out var error);

        Assert.False(ok);
        Assert.Equal("token_expired", error);
    }
}
