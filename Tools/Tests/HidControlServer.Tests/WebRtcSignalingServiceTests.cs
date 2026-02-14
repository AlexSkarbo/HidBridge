using HidControl.Infrastructure.Services;
using Xunit;

namespace HidControlServer.Tests;

public sealed class WebRtcSignalingServiceTests
{
    [Fact]
    public void NormalizeRoom_RejectsInvalidValues()
    {
        var svc = new WebRtcSignalingService();

        var empty = svc.NormalizeRoom("   ");
        var badChars = svc.NormalizeRoom("bad room");

        Assert.False(empty.Ok);
        Assert.Equal("room_required", empty.Error);
        Assert.False(badChars.Ok);
        Assert.Equal("room_invalid_chars", badChars.Error);
    }

    [Fact]
    public void TryJoin_EnforcesTwoPeerLimit()
    {
        var svc = new WebRtcSignalingService();

        var j1 = svc.TryJoin("control", "helper");
        var j2 = svc.TryJoin("control", "controller");
        var j3 = svc.TryJoin("control", "other");

        Assert.True(j1.Ok);
        Assert.True(j2.Ok);
        Assert.False(j3.Ok);
        Assert.Equal("room_full", j3.Error);
        Assert.Equal(2, j3.Peers);
    }

    [Fact]
    public void Leave_RemovesEmptyRoomFromSnapshot()
    {
        var svc = new WebRtcSignalingService();

        Assert.True(svc.TryJoin("control", "helper").Ok);
        Assert.True(svc.TryJoin("control", "controller").Ok);
        Assert.Equal(2, svc.GetRoomPeerCountsSnapshot()["control"]);

        svc.Leave("control", "helper");
        svc.Leave("control", "controller");

        Assert.DoesNotContain("control", svc.GetRoomPeerCountsSnapshot().Keys);
    }

    [Fact]
    public void TryJoin_AllowsHandoverAfterPeerLeaves()
    {
        var svc = new WebRtcSignalingService();

        Assert.True(svc.TryJoin("control", "helper").Ok);
        Assert.True(svc.TryJoin("control", "controller-a").Ok);
        Assert.False(svc.TryJoin("control", "controller-b").Ok);

        svc.Leave("control", "controller-a");
        var joinAfterLeave = svc.TryJoin("control", "controller-b");

        Assert.True(joinAfterLeave.Ok);
        Assert.Equal(2, joinAfterLeave.Peers);
    }

    [Theory]
    [InlineData("video", true)]
    [InlineData("video-demo", true)]
    [InlineData("hb-v-1234-abcd", true)]
    [InlineData("control", false)]
    [InlineData("hb-1234-abcd", false)]
    public void GetRoomKind_ClassifiesByNamingConvention(string room, bool isVideo)
    {
        var svc = new WebRtcSignalingService();

        var kind = svc.GetRoomKind(room);

        Assert.Equal(isVideo, kind == HidControl.Application.Abstractions.WebRtcRoomKind.Video);
    }
}
