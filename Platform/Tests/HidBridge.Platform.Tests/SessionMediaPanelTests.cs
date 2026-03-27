using Bunit;
using HidBridge.ControlPlane.Web.Components.Pages;
using HidBridge.ControlPlane.Web.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies regression-sensitive playback panel behaviors in the session room UI.
/// </summary>
public sealed class SessionMediaPanelTests : Bunit.TestContext
{
    public SessionMediaPanelTests()
    {
        Services.AddSingleton(new OperatorText());
    }

    /// <summary>
    /// Verifies playback action buttons stay guarded until playback URL is present.
    /// </summary>
    [Fact]
    public void Buttons_AreDisabledWithoutPlaybackUrl()
    {
        SetupMediaModule();
        var cut = RenderComponent<SessionMediaPanel>(parameters => parameters
            .Add(component => component.SessionId, "room-1")
            .Add(component => component.PlaybackUrl, (string?)null));

        var buttons = cut.FindAll("button");
        var start = Assert.Single(buttons, button => button.TextContent.Contains("Start stream", StringComparison.Ordinal));
        var stop = Assert.Single(buttons, button => button.TextContent.Contains("Stop stream", StringComparison.Ordinal));
        var reconnect = Assert.Single(buttons, button => button.TextContent.Contains("Reconnect now", StringComparison.Ordinal));

        Assert.True(start.HasAttribute("disabled"));
        Assert.True(stop.HasAttribute("disabled"));
        Assert.True(reconnect.HasAttribute("disabled"));
    }

    /// <summary>
    /// Verifies runtime playback events update visible connection and media state values.
    /// </summary>
    [Fact]
    public async Task OnPlaybackEvent_Started_UpdatesRuntimeStateIndicators()
    {
        SetupMediaModule();
        var cut = RenderComponent<SessionMediaPanel>(parameters => parameters
            .Add(component => component.SessionId, "room-1")
            .Add(component => component.PlaybackUrl, "http://127.0.0.1:8889/whep/edge-main"));

        await cut.InvokeAsync(() => cut.Instance.OnPlaybackEvent(new SessionMediaPanel.MediaPlaybackRuntimeEvent
        {
            Type = "started",
            Mode = "whep",
            ConnectionState = "connected",
            IceConnectionState = "connected",
            TrackCount = 2,
            Attempt = 1,
            IsRunning = true,
        }));

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Playback state: Running", markup, StringComparison.Ordinal);
            Assert.Contains("Connection state: connected", markup, StringComparison.Ordinal);
            Assert.Contains("ICE state: connected", markup, StringComparison.Ordinal);
            Assert.Contains("Tracks: 2", markup, StringComparison.Ordinal);
            Assert.Contains("Reconnect attempts: 1", markup, StringComparison.Ordinal);
            Assert.Contains("Last event: started", markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies playback error events are surfaced to the operator.
    /// </summary>
    [Fact]
    public async Task OnPlaybackEvent_Error_ShowsWarningBanner()
    {
        SetupMediaModule();
        var cut = RenderComponent<SessionMediaPanel>(parameters => parameters
            .Add(component => component.SessionId, "room-1")
            .Add(component => component.PlaybackUrl, "http://127.0.0.1:8889/whep/edge-main"));

        await cut.InvokeAsync(() => cut.Instance.OnPlaybackEvent(new SessionMediaPanel.MediaPlaybackRuntimeEvent
        {
            Type = "playback-error",
            Error = "WHEP negotiation failed (500).",
            IsRunning = false,
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Not playable reason: WHEP negotiation failed (500).", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Playback state: Stopped", cut.Markup, StringComparison.Ordinal);
        });
    }

    private void SetupMediaModule()
    {
        var module = JSInterop.SetupModule("./Components/Pages/SessionMediaPanel.razor.js");
        module.SetupVoid("stopPlayback", _ => true);
        module.SetupVoid("setMuted", _ => true);
        module.SetupVoid("setPlaybackOptions", _ => true);
    }
}
