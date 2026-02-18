using HidControl.Application.UseCases.WebRtc;
using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControlServer.Services;

namespace HidControlServer.Endpoints.Sys;

/// <summary>
/// Registers WebRTC status endpoints under <c>/status/webrtc/*</c>.
/// </summary>
public static class WebRtcStatusEndpoints
{
    /// <summary>
    /// Maps WebRTC status endpoints.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static void MapWebRtcStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/status/webrtc/ice", async ([Microsoft.AspNetCore.Mvc.FromServices] GetWebRtcIceConfigUseCase uc, CancellationToken ct) =>
        {
            var cfg = await uc.Execute(ct);
            var iceServers = cfg.IceServers
                .Select(s => new
                {
                    urls = s.Urls,
                    username = s.Username,
                    credential = s.Credential,
                    credentialType = s.CredentialType
                })
                .ToArray();

            return Results.Ok(new { ok = true, ttlSeconds = cfg.TtlSeconds, iceServers });
        });

        app.MapGet("/status/webrtc/config", async ([Microsoft.AspNetCore.Mvc.FromServices] GetWebRtcClientConfigUseCase uc, CancellationToken ct) =>
        {
            var cfg = await uc.Execute(ct);
            return Results.Ok(new
            {
                ok = true,
                joinTimeoutMs = cfg.JoinTimeoutMs,
                connectTimeoutMs = cfg.ConnectTimeoutMs,
                roomsCleanupIntervalSeconds = cfg.RoomsCleanupIntervalSeconds,
                roomIdleStopSeconds = cfg.RoomIdleStopSeconds,
                roomsMaxHelpers = cfg.RoomsMaxHelpers
            });
        });

        app.MapGet("/status/webrtc/rooms", async ([Microsoft.AspNetCore.Mvc.FromServices] ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms
                .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper, isControl = r.IsControl })
                .ToArray();
            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/rooms", async (HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] CreateWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            string? roomId = await ReadRequestedRoomIdAsync(req, ct);
            var res = await uc.Execute(roomId, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapDelete("/status/webrtc/rooms/{room}", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] DeleteWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        });

        app.MapGet("/status/webrtc/video/rooms", async ([Microsoft.AspNetCore.Mvc.FromServices] ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms
                .Where(r =>
                    string.Equals(r.Room, "video", StringComparison.OrdinalIgnoreCase) ||
                    r.Room.StartsWith("hb-v-", StringComparison.OrdinalIgnoreCase) ||
                    r.Room.StartsWith("video-", StringComparison.OrdinalIgnoreCase))
                .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper })
                .ToArray();
            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/video/rooms", async (HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] CreateWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            (string? roomId, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile) = await ReadRequestedVideoRoomAsync(req, ct);
            var res = await uc.Execute(roomId, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, audioEnabled, audioInput, audioBitrateKbps, streamProfile, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapPost("/status/webrtc/video/rooms/{room}/restart", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] RestartWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            (_, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile) = await ReadRequestedVideoRoomAsync(req, ct);
            var res = await uc.Execute(room, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, audioEnabled, audioInput, audioBitrateKbps, streamProfile, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapGet("/status/webrtc/video/rooms/{room}/profile", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] IWebRtcRoomsService svc, CancellationToken ct) =>
        {
            var res = await svc.GetVideoRoomProfileAsync(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, streamProfile = res.StreamProfile, error = res.Error });
        });

        app.MapPost("/status/webrtc/video/rooms/{room}/profile", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] IWebRtcRoomsService svc, CancellationToken ct) =>
        {
            WebRtcCreateVideoRoomRequest? body = null;
            try
            {
                body = await req.ReadFromJsonAsync<WebRtcCreateVideoRoomRequest>(cancellationToken: ct);
            }
            catch
            {
                body = null;
            }

            var res = await svc.SetVideoRoomProfileAsync(room, body?.StreamProfile, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, streamProfile = res.StreamProfile, error = res.Error });
        });

        app.MapDelete("/status/webrtc/video/rooms/{room}", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] DeleteWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        });

        app.MapGet("/status/webrtc/video/peers/{room}", (string room, [Microsoft.AspNetCore.Mvc.FromServices] WebRtcVideoPeerSupervisor sup) =>
        {
            var st = sup.GetRoomRuntimeStatus(room);
            if (st is null)
            {
                return Results.Ok(new { ok = false, room, error = "room_not_found" });
            }

            return Results.Ok(new
            {
                ok = true,
                room = st.Room,
                sourceModeRequested = st.SourceModeRequested,
                sourceModeActive = st.SourceModeActive,
                fallbackUsed = st.FallbackUsed,
                lastVideoError = st.LastVideoError,
                startupTimeoutReason = st.StartupTimeoutReason,
                startedAtUtc = st.StartedAtUtc,
                updatedAtUtc = st.UpdatedAtUtc,
                pid = st.Pid,
                running = st.Running,
                encoder = st.Encoder,
                codec = st.Codec,
                qualityPreset = st.QualityPreset,
                targetBitrateKbps = st.TargetBitrateKbps,
                targetFps = st.TargetFps,
                measuredFps = st.MeasuredFps,
                measuredKbps = st.MeasuredKbps,
                frames = st.Frames,
                packets = st.Packets,
                startupMs = st.StartupMs,
                audioEnabled = st.AudioEnabled,
                audioInput = st.AudioInput,
                audioBitrateKbps = st.AudioBitrateKbps,
                audioMeasuredKbps = st.AudioMeasuredKbps,
                audioRunning = st.AudioRunning
            });
        });

        app.MapPost("/status/webrtc/video/rooms/{room}/audio-probe", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] WebRtcVideoPeerSupervisor sup, CancellationToken ct) =>
        {
            WebRtcAudioProbeRequest? body = null;
            try
            {
                body = await req.ReadFromJsonAsync<WebRtcAudioProbeRequest>(cancellationToken: ct);
            }
            catch
            {
                body = null;
            }

            int durationSec = body?.DurationSec ?? 3;
            string? audioInput = body?.AudioInput;
            var result = await sup.RunAudioProbeAsync(room, audioInput, durationSec, ct);
            return Results.Ok(new
            {
                ok = result.Ok,
                room = result.Room,
                signal = result.Signal,
                selector = result.Selector,
                durationSec = result.DurationSec,
                bytes = result.Bytes,
                rms = result.Rms,
                levelPct = result.LevelPct,
                error = result.Error
            });
        });
    }

    /// <summary>
    /// Reads optional WebRTC room id from request body.
    /// </summary>
    /// <param name="req">Incoming request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Requested room id or null when absent/invalid.</returns>
    private static async Task<string?> ReadRequestedRoomIdAsync(HttpRequest req, CancellationToken ct)
    {
        try
        {
            var body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateRoomRequest>(cancellationToken: ct);
            return body?.Room;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads optional video room id and quality preset from request body.
    /// </summary>
    /// <param name="req">Incoming request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of room id and quality preset.</returns>
    private static async Task<(string? Room, string? QualityPreset, int? BitrateKbps, int? Fps, int? ImageQuality, string? CaptureInput, string? Encoder, string? Codec, bool? AudioEnabled, string? AudioInput, int? AudioBitrateKbps, string? StreamProfile)> ReadRequestedVideoRoomAsync(HttpRequest req, CancellationToken ct)
    {
        try
        {
            var body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateVideoRoomRequest>(cancellationToken: ct);
            return (body?.Room, body?.QualityPreset, body?.BitrateKbps, body?.Fps, body?.ImageQuality, body?.CaptureInput, body?.Encoder, body?.Codec, body?.AudioEnabled, body?.AudioInput, body?.AudioBitrateKbps, body?.StreamProfile);
        }
        catch
        {
            return (null, null, null, null, null, null, null, null, null, null, null, null);
        }
    }

    private sealed record WebRtcAudioProbeRequest(string? AudioInput, int? DurationSec);
}
