using HidControl.Application.UseCases.WebRtc;
using HidControl.Application.Abstractions;
using HidControl.Infrastructure.Services;
using Xunit;

namespace HidControlServer.Tests;

public sealed class WebRtcSignalingRoutingUseCaseTests
{
    private static HandleWebRtcSignalingMessageUseCase CreateUseCase()
    {
        var signaling = new WebRtcSignalingService();
        var join = new JoinWebRtcSignalingUseCase(signaling);
        var validate = new ValidateWebRtcSignalUseCase(signaling);
        var leave = new LeaveWebRtcSignalingUseCase(signaling);
        return new HandleWebRtcSignalingMessageUseCase(join, validate, leave);
    }

    [Fact]
    public void Join_RoutesToJoinAndUpdatesRoom()
    {
        var uc = CreateUseCase();

        var res = uc.Execute(currentRoom: null, requestedRoom: "control", clientId: "client-a", messageType: "join");

        Assert.True(res.Ok);
        Assert.Equal(WebRtcSignalingAction.Join, res.Action);
        Assert.Equal("control", res.Room);
        Assert.Equal("control", res.NextRoom);
        Assert.Equal(WebRtcRoomKind.Control, res.Kind);
        Assert.Equal(1, res.Peers);
    }

    [Fact]
    public void Signal_WhenNotJoined_IsDenied()
    {
        var uc = CreateUseCase();

        var res = uc.Execute(currentRoom: null, requestedRoom: null, clientId: "client-a", messageType: "signal");

        Assert.False(res.Ok);
        Assert.Equal(WebRtcSignalingAction.Signal, res.Action);
        Assert.Equal("not_joined", res.Error);
    }

    [Fact]
    public void Leave_WhenJoined_ReturnsPeerLeftBroadcast()
    {
        var uc = CreateUseCase();
        _ = uc.Execute(currentRoom: null, requestedRoom: "video", clientId: "client-a", messageType: "join");
        _ = uc.Execute(currentRoom: null, requestedRoom: "video", clientId: "client-b", messageType: "join");

        var res = uc.Execute(currentRoom: "video", requestedRoom: null, clientId: "client-b", messageType: "leave");

        Assert.True(res.Ok);
        Assert.Equal(WebRtcSignalingAction.Leave, res.Action);
        Assert.True(res.HasPeerLeftBroadcast);
        Assert.Equal("video", res.Room);
        Assert.Equal(WebRtcRoomKind.Video, res.Kind);
        Assert.Equal(1, res.PeersLeft);
    }

    [Fact]
    public void Ping_ReturnsOk()
    {
        var uc = CreateUseCase();

        var res = uc.Execute(currentRoom: "control", requestedRoom: null, clientId: "client-a", messageType: "ping");

        Assert.True(res.Ok);
        Assert.Equal(WebRtcSignalingAction.Ping, res.Action);
        Assert.Equal("control", res.NextRoom);
    }

    [Fact]
    public void UnsupportedType_ReturnsError()
    {
        var uc = CreateUseCase();

        var res = uc.Execute(currentRoom: "control", requestedRoom: null, clientId: "client-a", messageType: "foo");

        Assert.False(res.Ok);
        Assert.Equal(WebRtcSignalingAction.Unsupported, res.Action);
        Assert.Equal("unsupported_type", res.Error);
    }
}
