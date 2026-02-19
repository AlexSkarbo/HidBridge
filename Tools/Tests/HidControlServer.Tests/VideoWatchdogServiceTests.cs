using System.Diagnostics;
using System.Text.Json;
using HidControl.Contracts;
using HidControl.Core;
using HidControl.UseCases;
using HidControl.UseCases.Video;
using Xunit;

namespace HidControlServer.Tests;

public sealed class VideoWatchdogServiceTests
{
    private sealed class FakeController : IVideoFfmpegController
    {
        public int StartCalls { get; private set; }

        public FfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force)
        {
            StartCalls++;
            return new FfmpegStartResult(Array.Empty<object>(), Array.Empty<string>());
        }
    }

    private sealed class FakeEvents : IVideoWatchdogEvents
    {
        public List<(string Category, string Message, string Data)> Items { get; } = new();

        public void Log(string category, string message, object? data = null)
        {
            Items.Add((category, message, JsonSerializer.Serialize(data)));
        }
    }

    private sealed class FakeRuntime : IVideoRuntimeState
    {
        public Dictionary<string, bool> ManualStops { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Process> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FfmpegProcState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsManualStop(string sourceId) =>
            ManualStops.TryGetValue(sourceId, out var stopped) && stopped;

        public bool TryGetFfmpegProcess(string sourceId, out Process process)
        {
            return Processes.TryGetValue(sourceId, out process!);
        }

        public bool TryRemoveFfmpegProcess(string sourceId, out Process? process)
        {
            if (Processes.TryGetValue(sourceId, out var existing))
            {
                Processes.Remove(sourceId);
                process = existing;
                return true;
            }

            process = null;
            return false;
        }

        public FfmpegProcState GetOrAddFfmpegState(string sourceId)
        {
            if (!States.TryGetValue(sourceId, out var state))
            {
                state = new FfmpegProcState();
                States[sourceId] = state;
            }
            return state;
        }

        public FlvHubStats? GetFlvStats(string sourceId) => null;

        public void KillOrphansForSource(VideoSourceConfig source, Process? tracked)
        {
        }
    }

    [Fact]
    public void RunFfmpegWatchdog_WhenManualStop_LogsSkipReason_AndDoesNotRestart()
    {
        var controller = new FakeController();
        var events = new FakeEvents();
        var runtime = new FakeRuntime();
        runtime.ManualStops["cap-1"] = true;
        var svc = new VideoWatchdogService(new VideoFfmpegService(controller), events);
        var options = new VideoWatchdogOptions(10, 100, 1000, false, 5);
        var sources = new VideoSourceStore(new[]
        {
            new VideoSourceConfig("cap-1", "uvc", "win:device", "Capture", true)
        });
        var output = new VideoOutputState(Hls: true, Mjpeg: true, Flv: false, MjpegPassthrough: false, MjpegFps: 30, MjpegSize: "");

        svc.RunFfmpegWatchdog(options, sources, output, runtime);

        Assert.Equal(0, controller.StartCalls);
        Assert.Contains(events.Items, e => e.Message == "watchdog_skip_reason" && e.Data.Contains("\"reason\":\"manual_stop\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RunFfmpegWatchdog_WhenNoOutputsEnabled_LogsSkipReason_AndDoesNotRestart()
    {
        var controller = new FakeController();
        var events = new FakeEvents();
        var runtime = new FakeRuntime();
        var svc = new VideoWatchdogService(new VideoFfmpegService(controller), events);
        var options = new VideoWatchdogOptions(10, 100, 1000, false, 5);
        var sources = new VideoSourceStore(new[]
        {
            new VideoSourceConfig("cap-1", "uvc", "win:device", "Capture", true)
        });
        var output = new VideoOutputState(Hls: false, Mjpeg: false, Flv: false, MjpegPassthrough: false, MjpegFps: 30, MjpegSize: "");

        svc.RunFfmpegWatchdog(options, sources, output, runtime);

        Assert.Equal(0, controller.StartCalls);
        Assert.Contains(events.Items, e => e.Message == "watchdog_skip_reason" && e.Data.Contains("\"reason\":\"roomless_no_output_enabled\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RunFfmpegWatchdog_WhenCaptureLastExitMinus22_LogsSkipReason_AndDoesNotRestart()
    {
        var controller = new FakeController();
        var events = new FakeEvents();
        var runtime = new FakeRuntime();
        runtime.States["cap-1"] = new FfmpegProcState { LastExitCode = -22, RestartCount = 3 };
        var svc = new VideoWatchdogService(new VideoFfmpegService(controller), events);
        var options = new VideoWatchdogOptions(10, 100, 1000, false, 5);
        var sources = new VideoSourceStore(new[]
        {
            new VideoSourceConfig("cap-1", "uvc", "win:device", "Capture", true)
        });
        var output = new VideoOutputState(Hls: true, Mjpeg: true, Flv: false, MjpegPassthrough: false, MjpegFps: 30, MjpegSize: "");

        svc.RunFfmpegWatchdog(options, sources, output, runtime);

        Assert.Equal(0, controller.StartCalls);
        Assert.Contains(events.Items, e => e.Message == "watchdog_skip_reason" && e.Data.Contains("\"reason\":\"invalid_args_exit_-22\"", StringComparison.Ordinal));
    }
}
