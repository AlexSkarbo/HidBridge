using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.WebSockets;
using System.Threading.Channels;
using HidControl.Application.UseCases;
using HidControl.UseCases.Video;
using ServerVideoMode = HidControlServer.Services.VideoModeService;
using HidControlServer;
using HidControlServer.Services;
using VideoConfigHelpers = HidControlServer.Services.VideoConfigService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Encoding = global::System.Text.Encoding;

namespace HidControlServer.Endpoints.Video;

/// <summary>
/// Registers Video endpoints.
/// </summary>
public static class VideoEndpoints
{
    /// <summary>
    /// Maps video endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapVideoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/video");

        var appState = app.Services.GetRequiredService<AppState>();
        var runtime = app.Services.GetRequiredService<VideoRuntimeService>();
        var outputService = app.Services.GetRequiredService<VideoOutputService>();
        var ffmpegService = app.Services.GetRequiredService<VideoFfmpegService>();
        var configService = app.Services.GetRequiredService<HidControl.UseCases.Video.VideoConfigService>();

        group.MapGet("/sources", (GetVideoSourcesUseCase useCase) =>
        {
            return Results.Ok(new { ok = true, sources = useCase.Execute() });
        });

        group.MapPost("/sources", (VideoSourcesRequest req, SetVideoSourcesUseCase useCase) =>
        {
            var result = useCase.Execute(req);
            return Results.Ok(new { ok = result.Ok, configSaved = result.ConfigSaved, configPath = result.ConfigPath, configError = result.ConfigError });
        });

        group.MapPost("/sources/upsert", (VideoSourceConfig req, UpsertVideoSourceUseCase useCase) =>
        {
            var result = useCase.Execute(req);
            return Results.Ok(new { ok = result.Ok, configSaved = result.ConfigSaved, configPath = result.ConfigPath, configError = result.ConfigError });
        });

        group.MapGet("/profiles", (GetVideoProfilesUseCase useCase) =>
        {
            var snapshot = useCase.Execute();
            return Results.Ok(new { ok = true, active = snapshot.ActiveProfile, profiles = snapshot.Profiles });
        });

        group.MapGet("/modes", async (string? id, bool? refresh, GetVideoModesUseCase useCase, HttpContext ctx) =>
        {
            var result = await useCase.ExecuteAsync(id, refresh == true, ctx.RequestAborted);
            if (!result.Ok)
            {
                return Results.Ok(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new
            {
                ok = true,
                cached = result.Cached,
                source = result.Source,
                device = result.Device,
                modes = result.Modes.Select(m => new { width = m.Width, height = m.Height, maxFps = m.MaxFps }).ToList(),
                supportsMjpeg = result.SupportsMjpeg
            });
        });

        group.MapGet("/diag", (GetVideoDiagUseCase useCase) =>
        {
            return Results.Ok(useCase.Execute());
        });

        group.MapPost("/capture/stop", async (string? id, StopVideoCaptureUseCase useCase, HttpContext ctx) =>
        {
            var result = await useCase.ExecuteAsync(id, ctx.RequestAborted);
            if (!result.Ok)
            {
                return Results.Ok(new { ok = false, error = result.Error ?? "failed" });
            }
            ServerEventLog.Log("video.capture", "manual_stop", new { id, count = result.Stopped.Count });
            return Results.Ok(new { ok = true, stopped = result.Stopped, count = result.Stopped.Count });
        });

        group.MapPost("/profiles", (VideoProfilesRequest req, SetVideoProfilesUseCase useCase) =>
        {
            var snapshot = useCase.Execute(req.Profiles, req.Active);
            return Results.Ok(new { ok = true, active = snapshot.ActiveProfile });
        });

        group.MapPost("/profiles/active", (string? name, VideoProfileActiveRequest? req, SetActiveVideoProfileUseCase useCase, AppState appState) =>
        {
            string? target = name ?? req?.Name;
            if (string.IsNullOrWhiteSpace(target))
            {
                return Results.Ok(new { ok = false, error = "name is required" });
            }
            var result = useCase.Execute(target);
            string? configPath = result.ConfigPath ?? appState.ConfigPath;
            return Results.Ok(new
            {
                ok = result.Ok,
                active = result.Active,
                configSaved = result.ConfigSaved,
                configPath,
                configError = result.ConfigError
            });
        });

        group.MapGet("/streams", (GetVideoStreamsUseCase useCase) =>
        {
            var list = useCase.Execute();
            return Results.Ok(new { ok = true, streams = list });
        });

        group.MapGet("/output", (GetVideoOutputStatusUseCase useCase) =>
        {
            var result = useCase.Execute();
            var state = result.State;
            return Results.Ok(new
            {
                ok = result.Ok,
                hls = state.Hls,
                mjpeg = state.Mjpeg,
                flv = state.Flv,
                mjpegPassthrough = state.MjpegPassthrough,
                mjpegFps = state.MjpegFps,
                mjpegSize = state.MjpegSize,
                mode = result.Mode
            });
        });

        group.MapPost("/output", async (VideoOutputRequest req, ApplyVideoOutputUseCase useCase, HttpContext ctx) =>
        {
            var result = await useCase.ExecuteAsync(req, ctx.RequestAborted);
            if (!result.Ok)
            {
                return Results.Ok(new { ok = false, error = result.Error });
            }

            if (result.StoppedFfmpeg.Count > 0)
            {
                ServerEventLog.Log("ffmpeg", "stop_request:output_apply", new { ids = result.StoppedFfmpeg, anyRunning = result.AnyRunningBefore, mode = result.Mode });
            }
            ServerEventLog.Log("video.output", "apply", new { mode = result.Mode, sources = result.SourcesEligible, anyRunning = result.AnyRunningBefore, manualStops = result.ManualStops });

            return Results.Ok(new
            {
                ok = true,
                hls = result.State.Hls,
                mjpeg = result.State.Mjpeg,
                flv = result.State.Flv,
                mjpegPassthrough = result.State.MjpegPassthrough,
                mjpegFps = result.State.MjpegFps,
                mjpegSize = result.State.MjpegSize
            });
        });

        group.MapGet("/mjpeg/{id}", async (string id, int? fps, int? w, int? h, int? q, string? passthrough, Options opt, VideoSourceStore store, VideoProfileStore profiles, AppState appState, HttpContext ctx) =>
        {
            var source = store.GetAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("source not found");
                return;
            }
            var outputState = outputService.Get();
            if (!outputState.Mjpeg)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("mjpeg_disabled");
                return;
            }

            bool streamRunning = appState.FfmpegProcesses.TryGetValue(source.Id, out var streamProc) &&
                streamProc is not null &&
                !streamProc.HasExited;

            if (!opt.VideoSecondaryCaptureEnabled)
            {
                if (!streamRunning)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    await ctx.Response.WriteAsync("stream_not_running");
                    return;
                }
                VideoFrameHub hub = appState.VideoFrameHubs.GetOrAdd(source.Id, _ => new VideoFrameHub());
                byte[]? hubFirstFrameLocal = await hub.WaitForFrameAsync(TimeSpan.FromSeconds(5), ctx.RequestAborted);
                if (hubFirstFrameLocal is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await ctx.Response.WriteAsync("stream_no_frames");
                    return;
                }

                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                ctx.Response.Headers.CacheControl = "no-cache";

                ChannelReader<byte[]> hubReaderLocal = hub.Subscribe(ctx.RequestAborted);
                bool hubSentFirstLocal = false;
                await foreach (byte[] hubJpegFrame in hubReaderLocal.ReadAllAsync(ctx.RequestAborted))
                {
                    byte[] frameBytes = hubJpegFrame;
                    if (!hubSentFirstLocal)
                    {
                        frameBytes = hubFirstFrameLocal;
                        hubSentFirstLocal = true;
                    }
                    else if (ReferenceEquals(frameBytes, hubFirstFrameLocal))
                    {
                        continue;
                    }
                    string header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frameBytes.Length}\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                    await ctx.Response.Body.WriteAsync(headerBytes, ctx.RequestAborted);
                    await ctx.Response.Body.WriteAsync(frameBytes, ctx.RequestAborted);
                    await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                return;
            }

            string? input = VideoInputService.BuildMjpegInput(opt, source, appState);
            if (string.IsNullOrWhiteSpace(input))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("no input");
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            ctx.Response.Headers.CacheControl = "no-cache";

            bool rtspInput = input.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
            if (streamRunning && !rtspInput)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("stream_running_no_rtsp");
                return;
            }
            if (rtspInput && !opt.VideoSecondaryCaptureEnabled)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("secondary_capture_disabled");
                return;
            }
            if (!rtspInput)
            {
                bool lockFree = appState.VideoCaptureLock.Wait(0);
                if (lockFree)
                {
                    appState.VideoCaptureLock.Release();
                }
                else
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    await ctx.Response.WriteAsync("capture_busy");
                    return;
                }
            }
            VideoCaptureWorker worker = appState.VideoCaptureWorkers.GetOrAdd(source.Id, _ => new VideoCaptureWorker(opt, source, store, appState, outputService, ffmpegService));
            if (worker.IsRunning && !string.Equals(worker.CurrentInput, input, StringComparison.OrdinalIgnoreCase))
            {
                if (appState.VideoCaptureWorkers.TryRemove(source.Id, out var oldWorker))
                {
                    try { await oldWorker.DisposeAsync(); } catch { }
                }
                worker = appState.VideoCaptureWorkers.GetOrAdd(source.Id, _ => new VideoCaptureWorker(opt, source, store, appState, outputService, ffmpegService));
            }
            bool? passthroughFlag = null;
            if (!string.IsNullOrWhiteSpace(passthrough))
            {
                if (bool.TryParse(passthrough, out bool parsed))
                {
                    passthroughFlag = parsed;
                }
                else if (passthrough == "1")
                {
                    passthroughFlag = true;
                }
                else if (passthrough == "0")
                {
                    passthroughFlag = false;
                }
            }
            await worker.StartAsync(fps, w, h, q, passthroughFlag, ctx.RequestAborted);

            byte[]? workerFirstFrameLocal = await worker.WaitForFrameAsync(TimeSpan.FromSeconds(5), ctx.RequestAborted);
            if (workerFirstFrameLocal is null)
            {
                string error = worker.LastError ?? "ffmpeg produced no frames";
                if (rtspInput && appState.FfmpegProcesses.TryGetValue(source.Id, out var proc) && proc is not null && !proc.HasExited)
                {
                    error = "rtsp_no_frames";
                }
                if (passthroughFlag == true && string.IsNullOrWhiteSpace(worker.LastError))
                {
                    error = "passthrough_no_frames";
                }
                ctx.Response.StatusCode = StreamDiagService.IsDeviceBusy(error) ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsync(StreamDiagService.IsDeviceBusy(error) ? "device busy" : error);
                return;
            }

            ChannelReader<byte[]> workerReaderLocal = worker.Subscribe(ctx.RequestAborted);
            bool workerSentFirstLocal = false;
            await foreach (byte[] workerJpegFrame in workerReaderLocal.ReadAllAsync(ctx.RequestAborted))
            {
                byte[] frameBytes = workerJpegFrame;
                if (!workerSentFirstLocal)
                {
                    frameBytes = workerFirstFrameLocal;
                    workerSentFirstLocal = true;
                }
                else if (ReferenceEquals(frameBytes, workerFirstFrameLocal))
                {
                    continue;
                }
                string header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frameBytes.Length}\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                await ctx.Response.Body.WriteAsync(headerBytes, ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(frameBytes, ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        group.MapGet("/snapshot/{id}", async (string id, Options opt, VideoSourceStore store, VideoProfileStore profiles, AppState appState, HttpContext ctx) =>
        {
            var source = store.GetAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("source not found");
                return;
            }

            bool streamRunning = appState.FfmpegProcesses.TryGetValue(source.Id, out var streamProc) &&
                streamProc is not null &&
                !streamProc.HasExited;

            if (!opt.VideoSecondaryCaptureEnabled)
            {
                if (!streamRunning)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    await ctx.Response.WriteAsync("stream_not_running");
                    return;
                }
                VideoFrameHub hub = appState.VideoFrameHubs.GetOrAdd(source.Id, _ => new VideoFrameHub());
                byte[]? hubFrame = await hub.WaitForFrameAsync(TimeSpan.FromSeconds(5), ctx.RequestAborted);
                if (hubFrame is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await ctx.Response.WriteAsync("stream_no_frames");
                    return;
                }
                ctx.Response.ContentType = "image/jpeg";
                await ctx.Response.Body.WriteAsync(hubFrame, ctx.RequestAborted);
                return;
            }

            string? input = VideoInputService.BuildMjpegInput(opt, source, appState);
            if (string.IsNullOrWhiteSpace(input))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("no input");
                return;
            }
            bool rtspInput = input.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
            if (streamRunning && !rtspInput)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("stream_running_no_rtsp");
                return;
            }
            if (rtspInput && !opt.VideoSecondaryCaptureEnabled)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("secondary_capture_disabled");
                return;
            }
            VideoCaptureWorker worker = appState.VideoCaptureWorkers.GetOrAdd(source.Id, _ => new VideoCaptureWorker(opt, source, store, appState, outputService, ffmpegService));
            if (worker.IsRunning && !string.Equals(worker.CurrentInput, input, StringComparison.OrdinalIgnoreCase))
            {
                if (appState.VideoCaptureWorkers.TryRemove(source.Id, out var oldWorker))
                {
                    try { await oldWorker.DisposeAsync(); } catch { }
                }
                worker = appState.VideoCaptureWorkers.GetOrAdd(source.Id, _ => new VideoCaptureWorker(opt, source, store, appState, outputService, ffmpegService));
            }
            await worker.StartAsync(null, null, null, null, false, ctx.RequestAborted);

            byte[]? workerFrame = await worker.WaitForFrameAsync(TimeSpan.FromSeconds(5), ctx.RequestAborted);
            if (workerFrame is null)
            {
                string error = worker.LastError ?? "ffmpeg produced no frames";
                if (rtspInput && appState.FfmpegProcesses.TryGetValue(source.Id, out var proc) && proc is not null && !proc.HasExited)
                {
                    error = "rtsp_no_frames";
                }
                ctx.Response.StatusCode = StreamDiagService.IsDeviceBusy(error) ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsync(StreamDiagService.IsDeviceBusy(error) ? "device busy" : error);
                return;
            }
            ctx.Response.ContentType = "image/jpeg";
            await ctx.Response.Body.WriteAsync(workerFrame, ctx.RequestAborted);
        });

        group.MapMethods("/flv/{id}", new[] { "GET", "HEAD" }, async (string id, Options opt, VideoSourceStore store, VideoProfileStore profiles, AppState appState, HttpContext ctx) =>
        {
            var outputState = outputService.Get();
            if (!outputState.Flv)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "flv_disabled";
                await ctx.Response.WriteAsync("flv_disabled");
                return;
            }
            var source = store.GetAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.Headers["X-Video-Error"] = "source_not_found";
                await ctx.Response.WriteAsync("source not found");
                return;
            }
            bool streamRunning = appState.FfmpegProcesses.TryGetValue(source.Id, out var streamProc) &&
                streamProc is not null &&
                !streamProc.HasExited;
            if (!streamRunning && runtime.IsManualStop(source.Id))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "stream_stopped_manual";
                await ctx.Response.WriteAsync("stream_stopped_manual");
                return;
            }
            if (!streamRunning)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "stream_not_running";
                await ctx.Response.WriteAsync("stream_not_running");
                return;
            }
            FlvStreamHub hub = appState.FlvStreamHubs.GetOrAdd(source.Id, _ => new FlvStreamHub());
            byte[]? header = await hub.WaitForHeaderAsync(TimeSpan.FromSeconds(12), ctx.RequestAborted);
            if (header is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                ctx.Response.Headers["X-Video-Error"] = "stream_no_header";
                await ctx.Response.WriteAsync("stream_no_header");
                return;
            }
            // Do not report FLV as ready unless we have a fresh config + keyframe.
            var flvReadyDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            bool flvReady = false;
            while (DateTimeOffset.UtcNow < flvReadyDeadline && !ctx.RequestAborted.IsCancellationRequested)
            {
                var stats = hub.GetStats();
                var lastTagAt = stats.lastTagAtUtc;
                bool recent = lastTagAt != default && (DateTimeOffset.UtcNow - lastTagAt) < TimeSpan.FromSeconds(6);
                flvReady = stats.hasVideoConfig && stats.hasKeyframe && recent;
                if (flvReady) break;
                try
                {
                    await Task.Delay(200, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            if (!flvReady)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "flv_not_ready";
                await ctx.Response.WriteAsync("flv_not_ready");
                var stats = hub.GetStats();
                ServerEventLog.Log("flv", "not_ready_http", new
                {
                    id = source.Id,
                    stats.hasHeader,
                    stats.hasVideoConfig,
                    stats.hasKeyframe,
                    stats.tagsIn,
                    stats.tagsPublished,
                    stats.lastTagAtUtc
                });
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "video/x-flv";
            ctx.Response.Headers.CacheControl = "no-cache";
            if (HttpMethods.IsHead(ctx.Request.Method))
            {
                return;
            }
            foreach (byte[] tag in hub.GetInitialTags())
            {
                await ctx.Response.Body.WriteAsync(tag, ctx.RequestAborted);
            }
            ChannelReader<byte[]> reader = hub.Subscribe(ctx.RequestAborted);
            await foreach (byte[] tag in reader.ReadAllAsync(ctx.RequestAborted))
            {
                await ctx.Response.Body.WriteAsync(tag, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        group.MapGet("/ws/flv/{id}", async (string id, Options opt, VideoSourceStore store, VideoProfileStore profiles, AppState appState, HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("ws_required");
                return;
            }
            var outputState = outputService.Get();
            if (!outputState.Flv)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("flv_disabled");
                return;
            }
            var source = store.GetAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("source not found");
                return;
            }
            bool streamRunning = appState.FfmpegProcesses.TryGetValue(source.Id, out var streamProc) &&
                streamProc is not null &&
                !streamProc.HasExited;
            if (!streamRunning && runtime.IsManualStop(source.Id))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "stream_stopped_manual";
                await ctx.Response.WriteAsync("stream_stopped_manual");
                return;
            }
            if (!streamRunning)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers["X-Video-Error"] = "stream_not_running";
                await ctx.Response.WriteAsync("stream_not_running");
                return;
            }
            FlvStreamHub hub = appState.FlvStreamHubs.GetOrAdd(source.Id, _ => new FlvStreamHub());
            byte[]? header = await hub.WaitForHeaderAsync(TimeSpan.FromSeconds(12), ctx.RequestAborted);
            if (header is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsync("stream_no_header");
                return;
            }
            // Ensure we have a usable AVC config + keyframe before upgrading to WS-FLV.
            // This avoids flv.js immediately disconnecting on partial startup.
            var readyDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
            bool wsReady = false;
            while (DateTimeOffset.UtcNow < readyDeadline && !ctx.RequestAborted.IsCancellationRequested)
            {
                var stats = hub.GetStats();
                var lastTagAt = stats.lastTagAtUtc;
                bool recent = lastTagAt != default && (DateTimeOffset.UtcNow - lastTagAt) < TimeSpan.FromSeconds(6);
                wsReady = stats.hasVideoConfig && stats.hasKeyframe && recent;
                if (wsReady)
                {
                    break;
                }
                try
                {
                    await Task.Delay(200, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            if (!wsReady)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("flv_not_ready");
                var stats = hub.GetStats();
                ServerEventLog.Log("flv", "not_ready_ws", new
                {
                    id = source.Id,
                    stats.hasHeader,
                    stats.hasVideoConfig,
                    stats.hasKeyframe,
                    stats.tagsIn,
                    stats.tagsPublished,
                    stats.lastTagAtUtc
                });
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            ServerEventLog.Log("flv", "ws_open", new { id = source.Id });
            // Use our own CTS so Subscribe() is not immediately canceled by a
            // transient RequestAborted state; we still honor abort via register.
            using var subCts = new CancellationTokenSource();
            using var abortReg = ctx.RequestAborted.Register(static s => ((CancellationTokenSource)s!).Cancel(), subCts);
            var subToken = subCts.Token;
            // Subscribe before sending init tags so diag can see an active subscriber.
            ChannelReader<byte[]> reader = hub.Subscribe(subToken);
            try
            {
                foreach (byte[] tag in hub.GetInitialTags())
                {
                    if (ws.State != WebSocketState.Open) break;
                    try
                    {
                        await ws.SendAsync(tag, WebSocketMessageType.Binary, true, subToken);
                    }
                    catch (OperationCanceledException)
                    {
                        subCts.Cancel();
                        break;
                    }
                    catch (WebSocketException)
                    {
                        subCts.Cancel();
                        break;
                    }
                }
                await foreach (byte[] tag in reader.ReadAllAsync(subToken))
                {
                    if (ws.State != WebSocketState.Open) break;
                    try
                    {
                        await ws.SendAsync(tag, WebSocketMessageType.Binary, true, subToken);
                    }
                    catch (OperationCanceledException)
                    {
                        subCts.Cancel();
                        break;
                    }
                    catch (WebSocketException)
                    {
                        subCts.Cancel();
                        break;
                    }
                }
            }
            finally
            {
                subCts.Cancel();
                ServerEventLog.Log("flv", "ws_close", new { id = source.Id });
            }
        });

        group.MapGet("/hls/{id}/{**path}", async (string id, string? path, Options opt, HttpContext ctx) =>
        {
            var outputState = outputService.Get();
            if (!outputState.Hls)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("hls_disabled");
                ServerEventLog.Log("hls", "disabled", new { id, path });
                return;
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            string file = string.IsNullOrWhiteSpace(path) ? "index.m3u8" : path;
            string fullPath = Path.Combine(opt.VideoHlsDir, id, file);
            if (!File.Exists(fullPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ServerEventLog.Log("hls", "missing", new { id, file });
                return;
            }
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            ctx.Response.ContentType = ext switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl",
                ".ts" => "video/mp2t",
                ".m4s" => "video/iso.segment",
                ".mp4" => "video/mp4",
                _ => "application/octet-stream"
            };
            await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });

        group.MapGet("/streams/config", (GetVideoStreamsConfigUseCase useCase) =>
        {
            var list = useCase.Execute();
            return Results.Ok(new { ok = true, streams = list });
        });

        group.MapPost("/streams/config/write", (Options opt, GetVideoStreamsConfigUseCase useCase) =>
        {
            var list = useCase.Execute();
            Directory.CreateDirectory(opt.VideoLogDir);
            string path = Path.Combine(opt.VideoLogDir, "video_streams.json");
            File.WriteAllText(path, global::System.Text.Json.JsonSerializer.Serialize(list));
            return Results.Ok(new { ok = true, path });
        });

        group.MapPost("/ffmpeg/start", async (FfmpegStartRequest? req, StartFfmpegUseCase useCase, HttpContext ctx) =>
        {
            var result = await useCase.ExecuteAsync(req, ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                if (string.Equals(result.Error, "manual_stop", StringComparison.OrdinalIgnoreCase))
                {
                    ServerEventLog.Log("ffmpeg", "blocked_manual_stop", new { blocked = result.Blocked });
                }
                else
                {
                    ServerEventLog.Log("ffmpeg", "start_failed", new { error = result.Error, blocked = result.Blocked });
                }
                return Results.Ok(new { ok = false, error = result.Error, blocked = result.Blocked });
            }
            ServerEventLog.Log("ffmpeg", "start", new { started = result.Started, stoppedWorkers = result.StoppedWorkers });
            return Results.Ok(new
            {
                ok = result.Ok,
                started = result.Started,
                errors = result.Errors,
                killedOrphans = result.KilledOrphans.Select(k => new { id = k.SourceId, pids = k.Pids }).ToList(),
                stoppedWorkers = result.StoppedWorkers
            });
        });

        group.MapPost("/ffmpeg/stop", (string? id, StopFfmpegUseCase useCase, HttpContext ctx) =>
        {
            ServerEventLog.Log("ffmpeg", "stop_request:manual", new { id });
            var result = useCase.Execute(id);
            if (!result.Ok)
            {
                ServerEventLog.Log("ffmpeg", "stop_failed", new { id, error = result.Error });
                return Results.Ok(new { ok = false, error = result.Error });
            }
            if (string.IsNullOrWhiteSpace(result.Id))
            {
                ServerEventLog.Log("ffmpeg", "stop_all", new { status = result.Status });
                return Results.Ok(new { ok = true, status = result.Status });
            }
            ServerEventLog.Log("ffmpeg", "stop", new { id = result.Id, status = result.Status });
            return Results.Ok(new { ok = true, status = result.Status, id = result.Id });
        });

        group.MapGet("/ffmpeg/status", (GetFfmpegStatusUseCase useCase) =>
        {
            var result = useCase.Execute();
            return Results.Ok(new { ok = result.Ok, processes = result.Processes, states = result.States });
        });

        group.MapPost("/test-capture", async (string? id, int? timeoutSec, TestVideoCaptureUseCase useCase, HttpContext ctx) =>
        {
            var result = await useCase.ExecuteAsync(id, timeoutSec, ctx.RequestAborted);
            return Results.Ok(new
            {
                ok = result.Ok,
                source = result.Source,
                skipped = result.Skipped,
                workerLatestFrameAt = result.WorkerLatestFrameAt,
                pid = result.Pid,
                exitCode = result.ExitCode,
                args = result.Args,
                stderr = result.Stderr,
                error = result.Error
            });
        });

        group.MapPost("/sources/scan", (bool? apply, VideoSourceStore store) =>
        {
            var sources = new List<VideoSourceConfig>();
            var warnings = new List<string>();

            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    foreach (string path in Directory.GetFiles("/dev", "video*"))
                    {
                        string id = Path.GetFileName(path);
                        sources.Add(new VideoSourceConfig(id, "uvc", path, id, true));
                    }
                }
                else if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='Image' OR PNPClass='Camera' OR Service='usbvideo'");
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string? name = obj["Name"] as string;
                            string? deviceId = obj["DeviceID"] as string;
                            if (string.IsNullOrWhiteSpace(deviceId)) continue;
                            string id = "win-" + deviceId.GetHashCode().ToString("X8");
                            string url = "win:" + deviceId;
                            sources.Add(new VideoSourceConfig(id, "uvc", url, name, true));
                        }
                        if (sources.Count == 0)
                        {
                            warnings.Add("no UVC devices detected (Windows)");
                        }
                        else
                        {
                            warnings.Add("Windows scan uses symbolic ids; map to real capture devices in your video pipeline");
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Windows scan failed: " + ex.Message);
                    }
                }
                else
                {
                    warnings.Add("scan not supported on this OS");
                }
            }
            catch (Exception ex)
            {
                warnings.Add("scan failed: " + ex.Message);
            }

            if (apply == true)
            {
                store.ReplaceAll(sources);
                configService.SaveSources(store.GetAll());
            }

            return Results.Ok(new { ok = true, sources, warnings });
        });

        group.MapGet("/sources/windows", () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.Ok(new { ok = false, error = "not supported" });
            }

            var list = new List<object>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='Image' OR PNPClass='Camera' OR Service='usbvideo'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    string? deviceId = obj["DeviceID"] as string;
                    string? pnpDeviceId = obj["PNPDeviceID"] as string;
                    string? classGuid = obj["ClassGuid"] as string;
                    string? service = obj["Service"] as string;
                    list.Add(new
                    {
                        name,
                        deviceId,
                        pnpDeviceId,
                        classGuid,
                        service,
                        url = deviceId is null ? null : "win:" + deviceId
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }

            return Results.Ok(new { ok = true, devices = list });
        });

        group.MapGet("/dshow/devices", (Options opt) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.Ok(new { ok = false, error = "windows_only" });
            }
            try
            {
                var devices = ServerVideoMode.ListDshowDevices(opt.FfmpegPath);
                return Results.Ok(new { ok = true, devices });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });
    }
}
