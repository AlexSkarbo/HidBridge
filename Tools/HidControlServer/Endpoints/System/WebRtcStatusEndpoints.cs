using HidControl.Application.UseCases.WebRtc;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
        app.MapGet("/status/webrtc/ice", async (GetWebRtcIceConfigUseCase uc, CancellationToken ct) =>
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

        app.MapGet("/status/webrtc/config", async (GetWebRtcClientConfigUseCase uc, CancellationToken ct) =>
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

        app.MapGet("/status/webrtc/rooms", async (ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms
                .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper, isControl = r.IsControl })
                .ToArray();
            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/rooms", async (HttpRequest req, CreateWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            string? roomId = await ReadRequestedRoomIdAsync(req, ct);
            var res = await uc.Execute(roomId, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapDelete("/status/webrtc/rooms/{room}", async (string room, DeleteWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        });

        app.MapGet("/status/webrtc/video/rooms", async (ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
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

        app.MapPost("/status/webrtc/video/rooms", async (HttpRequest req, CreateWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            (string? roomId, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec) = await ReadRequestedVideoRoomAsync(req, ct);
            var res = await uc.Execute(roomId, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapPost("/status/webrtc/video/rooms/{room}/restart", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] RestartWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            (_, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec) = await ReadRequestedVideoRoomAsync(req, ct);
            var res = await uc.Execute(room, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapDelete("/status/webrtc/video/rooms/{room}", async (string room, DeleteWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        });

        app.MapGet("/status/webrtc/video/peers/{room}", (string room, WebRtcVideoPeerSupervisor sup) =>
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
                startupMs = st.StartupMs
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
    private static async Task<(string? Room, string? QualityPreset, int? BitrateKbps, int? Fps, int? ImageQuality, string? CaptureInput, string? Encoder, string? Codec)> ReadRequestedVideoRoomAsync(HttpRequest req, CancellationToken ct)
    {
        try
        {
            var body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateVideoRoomRequest>(cancellationToken: ct);
            return (body?.Room, body?.QualityPreset, body?.BitrateKbps, body?.Fps, body?.ImageQuality, body?.CaptureInput, body?.Encoder, body?.Codec);
        }
        catch
        {
            return (null, null, null, null, null, null, null, null);
        }
    }
}
