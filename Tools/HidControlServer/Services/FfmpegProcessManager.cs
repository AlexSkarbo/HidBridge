using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace HidControlServer.Services;

/// <summary>
/// Manages ffmpeg process discovery, cleanup, and lifecycle utilities.
/// </summary>
internal static class FfmpegProcessManager
{
    /// <summary>
    /// Stops all tracked ffmpeg processes and clears state.
    /// </summary>
    /// <param name="appState">Application state.</param>
    /// <returns>Stopped source IDs.</returns>
    public static List<string> StopFfmpegProcesses(AppState appState)
    {
        var stopped = new List<string>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var kvp in appState.FfmpegProcesses)
        {
            try { kvp.Value.Kill(true); } catch { }
            stopped.Add(kvp.Key);
        }
        foreach (var state in appState.FfmpegStates.Values)
        {
            state.LastExitAt = now;
            if (state.CaptureLockHeld)
            {
                try { appState.VideoCaptureLock.Release(); } catch { }
                state.CaptureLockHeld = false;
            }
        }
        appState.FfmpegProcesses.Clear();
        appState.FfmpegStates.Clear();
        return stopped;
    }

    /// <summary>
    /// Kills orphan ffmpeg processes for a source when configured.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="src">Video source.</param>
    /// <param name="tracked">Tracked process, if any.</param>
    /// <returns>Killed process IDs.</returns>
    public static List<int> KillFfmpegOrphansForSource(Options opt, VideoSourceConfig src, Process? tracked)
    {
        var killed = new List<int>();
        if (!opt.VideoKillOrphanFfmpeg)
        {
            return killed;
        }

        foreach (var info in ListFfmpegProcessInfos())
        {
            if (tracked is not null && info.Pid == tracked.Id)
            {
                continue;
            }
            if (!CmdMatchesSource(info.CommandLine, src))
            {
                continue;
            }
            try
            {
                using var proc = Process.GetProcessById(info.Pid);
                proc.Kill(true);
                killed.Add(info.Pid);
            }
            catch
            {
                // best-effort
            }
        }
        return killed;
    }

    /// <summary>
    /// Returns orphan ffmpeg process metadata for known sources.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="sources">Video sources.</param>
    /// <param name="ffmpegProcesses">Tracked ffmpeg processes.</param>
    /// <returns>List of orphan metadata objects.</returns>
    public static List<object> GetFfmpegOrphanInfos(Options opt, IReadOnlyList<VideoSourceConfig> sources, ConcurrentDictionary<string, Process> ffmpegProcesses)
    {
        var results = new List<object>();
        foreach (var info in ListFfmpegProcessInfos())
        {
            foreach (var src in sources)
            {
                ffmpegProcesses.TryGetValue(src.Id, out var tracked);
                if (tracked is not null && info.Pid == tracked.Id)
                {
                    continue;
                }
                if (!CmdMatchesSource(info.CommandLine, src))
                {
                    continue;
                }
                results.Add(new { id = src.Id, pid = info.Pid, cmd = info.CommandLine });
                break;
            }
        }
        return results;
    }

    /// <summary>
    /// Lists ffmpeg processes and their command lines.
    /// </summary>
    /// <returns>Process info enumeration.</returns>
    internal static IEnumerable<ProcInfo> ListFfmpegProcessInfos()
    {
        var results = new List<ProcInfo>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, Name FROM Win32_Process WHERE Name='ffmpeg.exe'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["ProcessId"] is not uint pid) continue;
                    string? cmd = obj["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(cmd)) continue;
                    results.Add(new ProcInfo((int)pid, cmd));
                }
            }
            catch
            {
                return results;
            }
            return results;
        }

        try
        {
            foreach (string dir in Directory.GetDirectories("/proc"))
            {
                string name = Path.GetFileName(dir);
                if (!int.TryParse(name, out int pid)) continue;
                string cmdlinePath = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdlinePath)) continue;
                string raw = File.ReadAllText(cmdlinePath);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string cmd = raw.Replace('\0', ' ').Trim();
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                if (!cmd.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new ProcInfo(pid, cmd));
            }
        }
        catch
        {
            return results;
        }
        return results;
    }

    /// <summary>
    /// Matches command line against a source signature.
    /// </summary>
    /// <param name="cmd">Command line.</param>
    /// <param name="src">Video source.</param>
    /// <returns>True if it matches.</returns>
    internal static bool CmdMatchesSource(string cmd, VideoSourceConfig src)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        if (!string.IsNullOrWhiteSpace(src.Id) &&
            cmd.Contains(src.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(src.Name) &&
            cmd.Contains(src.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(src.Url))
        {
            string url = src.Url.Replace("win:", "", StringComparison.OrdinalIgnoreCase);
            if (cmd.Contains(url, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        if (!string.IsNullOrWhiteSpace(src.FfmpegInputOverride) &&
            cmd.Contains(src.FfmpegInputOverride, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Lightweight ffmpeg process info.
    /// </summary>
    /// <param name="Pid">Process ID.</param>
    /// <param name="CommandLine">Command line.</param>
    internal readonly record struct ProcInfo(int Pid, string CommandLine);
}
