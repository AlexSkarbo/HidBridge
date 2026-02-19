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
        var group = app.MapGroup("/status/webrtc")
            .WithTags("WebRTC");

        group.MapGet("/ice", async ([Microsoft.AspNetCore.Mvc.FromServices] GetWebRtcIceConfigUseCase uc, CancellationToken ct) =>
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
        })
            .WithSummary("Get ICE configuration")
            .WithDescription("Returns STUN/TURN configuration for WebRTC clients.");

        group.MapGet("/config", async ([Microsoft.AspNetCore.Mvc.FromServices] GetWebRtcClientConfigUseCase uc, CancellationToken ct) =>
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
        })
            .WithSummary("Get WebRTC client config")
            .WithDescription("Returns timeouts and helper limits used by the browser client.");

        group.MapGet("/rooms", async ([Microsoft.AspNetCore.Mvc.FromServices] ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms
                .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper, isControl = r.IsControl })
                .ToArray();
            return Results.Ok(new { ok = true, rooms });
        })
            .WithSummary("List all WebRTC rooms")
            .WithDescription("Returns control and video rooms with peer/helper state.");

        group.MapPost("/rooms", async (HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] CreateWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            string? roomId = await ReadRequestedRoomIdAsync(req, ct);
            var res = await uc.Execute(roomId, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error, streamProfile = res.StreamProfile });
        })
            .WithSummary("Create generic WebRTC room")
            .WithDescription("Creates a room and optionally starts helper for control scenarios.");

        group.MapDelete("/rooms/{room}", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] DeleteWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        })
            .WithSummary("Delete generic WebRTC room")
            .WithDescription("Stops helper (if running) and removes generic room metadata.");

        group.MapGet("/video/rooms", async ([Microsoft.AspNetCore.Mvc.FromServices] ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            string opId = Guid.NewGuid().ToString("N")[..8];
            try
            {
                var snap = await uc.Execute(ct);
                var rooms = snap.Rooms
                    .Where(r =>
                        string.Equals(r.Room, "video", StringComparison.OrdinalIgnoreCase) ||
                        r.Room.StartsWith("hb-v-", StringComparison.OrdinalIgnoreCase) ||
                        r.Room.StartsWith("video-", StringComparison.OrdinalIgnoreCase))
                    .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper })
                    .ToArray();
                ServerEventLog.Log("webrtc.rooms", "list_video", new { opId, ok = true, count = rooms.Length });
                return Results.Ok(new { ok = true, rooms });
            }
            catch (OperationCanceledException)
            {
                ServerEventLog.Log("webrtc.rooms", "list_video", new { opId, ok = false, error = "canceled" });
                return Results.Ok(new { ok = false, rooms = Array.Empty<object>(), error = "canceled" });
            }
        })
            .WithSummary("List video rooms")
            .WithDescription("Returns only video rooms used by video streaming workflows.");

        group.MapPost("/video/rooms", async (HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] CreateWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            string opId = Guid.NewGuid().ToString("N")[..8];
            string? webOpId = req.Headers.TryGetValue("X-Web-OpId", out var h) ? h.ToString() : null;
            try
            {
                (string? roomId, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile) = await ReadRequestedVideoRoomAsync(req, ct);
                var res = await uc.Execute(roomId, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, audioEnabled, audioInput, audioBitrateKbps, streamProfile, ct);
                ServerEventLog.Log("webrtc.rooms", "create_video", new { opId, webOpId, requestedRoom = roomId, room = res.Room, ok = res.Ok, started = res.Started, error = res.Error, profile = res.StreamProfile });
                return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error, streamProfile = res.StreamProfile });
            }
            catch (OperationCanceledException)
            {
                ServerEventLog.Log("webrtc.rooms", "create_video", new { opId, webOpId, ok = false, error = "canceled" });
                return Results.Ok(new { ok = false, room = string.Empty, started = false, pid = (int?)null, error = "canceled", streamProfile = (string?)null });
            }
        })
            .WithSummary("Create video room")
            .WithDescription("Creates a video room and starts WebRtcVideoPeer with requested stream profile and overrides.");

        group.MapPost("/video/rooms/{room}/restart", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] RestartWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            string opId = Guid.NewGuid().ToString("N")[..8];
            try
            {
                (_, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile) = await ReadRequestedVideoRoomAsync(req, ct);
                var res = await uc.Execute(room, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, audioEnabled, audioInput, audioBitrateKbps, streamProfile, ct);
                ServerEventLog.Log("webrtc.rooms", "restart_video", new { opId, room = res.Room, ok = res.Ok, stopped = res.Stopped, started = res.Started, error = res.Error, profile = res.StreamProfile });
                return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, started = res.Started, pid = res.Pid, error = res.Error, streamProfile = res.StreamProfile });
            }
            catch (OperationCanceledException)
            {
                ServerEventLog.Log("webrtc.rooms", "restart_video", new { opId, room, ok = false, error = "canceled" });
                return Results.Ok(new { ok = false, room, stopped = false, started = false, pid = (int?)null, error = "canceled", streamProfile = (string?)null });
            }
        })
            .WithSummary("Restart video room")
            .WithDescription("Stops and starts the room helper while applying optional stream profile overrides.");

        group.MapGet("/video/rooms/{room}/profile", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] IWebRtcRoomsService svc, CancellationToken ct) =>
        {
            var res = await svc.GetVideoRoomProfileAsync(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, streamProfile = res.StreamProfile, error = res.Error });
        })
            .WithSummary("Get video room profile")
            .WithDescription("Returns effective stream profile binding for the room.");

        group.MapPost("/video/rooms/{room}/profile", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] IWebRtcRoomsService svc, CancellationToken ct) =>
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
        })
            .WithSummary("Set video room profile")
            .WithDescription("Sets or clears stream profile binding for the room.");

        group.MapDelete("/video/rooms/{room}", async (string room, [Microsoft.AspNetCore.Mvc.FromServices] DeleteWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            string opId = Guid.NewGuid().ToString("N")[..8];
            try
            {
                var res = await uc.Execute(room, ct);
                ServerEventLog.Log("webrtc.rooms", "delete_video", new { opId, room = res.Room, ok = res.Ok, stopped = res.Stopped, error = res.Error });
                return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
            }
            catch (OperationCanceledException)
            {
                ServerEventLog.Log("webrtc.rooms", "delete_video", new { opId, room, ok = false, error = "canceled" });
                return Results.Ok(new { ok = false, room, stopped = false, error = "canceled" });
            }
        })
            .WithSummary("Delete video room")
            .WithDescription("Stops helper and removes room/runtime/profile binding state.");

        group.MapGet("/video/peers/{room}", (string room, [Microsoft.AspNetCore.Mvc.FromServices] WebRtcVideoPeerSupervisor sup) =>
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
        })
            .WithSummary("Get video peer runtime state")
            .WithDescription("Returns detailed helper runtime telemetry for a specific video room.");

        group.MapPost("/video/rooms/{room}/audio-probe", async (string room, HttpRequest req, [Microsoft.AspNetCore.Mvc.FromServices] WebRtcVideoPeerSupervisor sup, CancellationToken ct) =>
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
                probeId = result.ProbeId,
                fileUrl = result.Ok && !string.IsNullOrWhiteSpace(result.ProbeId)
                    ? $"/status/webrtc/video/rooms/{Uri.EscapeDataString(room)}/audio-probe/file/{Uri.EscapeDataString(result.ProbeId!)}"
                    : null,
                error = result.Error
            });
        })
            .WithSummary("Run audio probe")
            .WithDescription("Captures short WAV sample and computes basic audio health metrics for selected room/input.");

        group.MapGet("/video/rooms/{room}/audio-probe/file/{probeId}", (string room, string probeId, [Microsoft.AspNetCore.Mvc.FromServices] WebRtcVideoPeerSupervisor sup) =>
        {
            var file = sup.GetAudioProbeFile(room, probeId);
            if (!file.Ok || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.FileName))
            {
                return Results.NotFound(new { ok = false, room, error = file.Error ?? "probe_not_found" });
            }
            return Results.File(file.Path, "audio/wav", file.FileName);
        })
            .WithSummary("Download audio probe file")
            .WithDescription("Downloads generated WAV file by probe id.");
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
