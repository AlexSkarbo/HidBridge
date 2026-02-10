using System.Diagnostics;
using HidControl.Application.Abstractions;

namespace HidControlServer.Services;

/// <summary>
/// Supervises the external <c>Tools/WebRtcVideoPeer</c> helper process.
/// 
/// This is a skeleton: the peer is responsible for establishing a WebRTC connection and (later)
/// publishing media tracks. For now, it can connect and open a DataChannel.
/// </summary>
public sealed class WebRtcVideoPeerSupervisor : IDisposable
{
    private readonly Options _opt;
    private readonly IWebRtcSignalingService _signaling;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProcState> _procs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _cleanupTimer;

    private sealed record ProcState(Process Process, DateTimeOffset StartedAtUtc, DateTimeOffset? IdleSinceUtc);

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="opt">Server options.</param>
    /// <param name="signaling">Signaling room state service.</param>
    public WebRtcVideoPeerSupervisor(Options opt, IWebRtcSignalingService signaling)
    {
        _opt = opt;
        _signaling = signaling;
        _cleanupTimer = new Timer(_ => CleanupTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(Math.Max(1, _opt.WebRtcRoomsCleanupIntervalSeconds)));
    }

    /// <summary>
    /// Starts the helper if enabled by config.
    /// </summary>
    public void StartIfEnabled()
    {
        if (!_opt.WebRtcVideoPeerAutoStart)
        {
            return;
        }

        string room = string.IsNullOrWhiteSpace(_opt.WebRtcVideoPeerRoom) ? "video" : _opt.WebRtcVideoPeerRoom;
        EnsureStarted(room);
    }

    /// <summary>
    /// Stops all helper processes.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            foreach (var kvp in _procs.ToArray())
            {
                try
                {
                    if (!kvp.Value.Process.HasExited)
                    {
                        kvp.Value.Process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                finally
                {
                    try { kvp.Value.Process.Dispose(); } catch { }
                    _procs.Remove(kvp.Key);
                }
            }
        }
    }

    /// <summary>
    /// Ensures a helper is running for the specified room.
    /// </summary>
    public (bool ok, bool started, int? pid, string? error) EnsureStarted(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return (false, false, null, "room_required");
        }

        lock (_lock)
        {
            int max = _opt.WebRtcRoomsMaxHelpers;
            if (max > 0 && !_procs.ContainsKey(room) && _procs.Count >= max)
            {
                return (false, false, null, "max_helpers_reached");
            }

            if (_procs.TryGetValue(room, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    return (true, false, existing.Process.Id, null);
                }

                try { existing.Process.Dispose(); } catch { }
                _procs.Remove(room);
            }

            if (!TryFindToolDir(out string toolDir, out string toolHint))
            {
                ServerEventLog.Log("webrtc.videopeer", "autostart_skipped", new { reason = "tool_not_found", hint = toolHint });
                return (false, false, null, "tool_not_found");
            }

            string serverUrl = BuildLocalServerUrl(_opt.Url);
            string token = _opt.Token ?? string.Empty;

            var psi = BuildStartInfo(toolDir, serverUrl, room, token, _opt.WebRtcVideoPeerStun);
            if (psi is null)
            {
                ServerEventLog.Log("webrtc.videopeer", "autostart_skipped", new { reason = "unsupported_os", os = Environment.OSVersion.Platform.ToString() });
                return (false, false, null, "unsupported_os");
            }

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (_, __) =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    ServerEventLog.Log("webrtc.videopeer", "exit", new { room, code });

                    lock (_lock)
                    {
                        if (_procs.TryGetValue(room, out var st) && ReferenceEquals(st.Process, p))
                        {
                            _procs.Remove(room);
                        }
                    }
                };

                if (!p.Start())
                {
                    ServerEventLog.Log("webrtc.videopeer", "autostart_failed", new { room, reason = "start_returned_false" });
                    return (false, false, null, "start_failed");
                }

                _procs[room] = new ProcState(p, DateTimeOffset.UtcNow, IdleSinceUtc: null);
                string logPath = BuildHelperLogPath(toolDir, room);
                ServerEventLog.Log("webrtc.videopeer", "autostart_started", new
                {
                    serverUrl,
                    room,
                    stun = _opt.WebRtcVideoPeerStun,
                    pid = p.Id,
                    exe = psi.FileName,
                    args = psi.Arguments,
                    logPath
                });
                return (true, true, p.Id, null);
            }
            catch (Exception ex)
            {
                ServerEventLog.Log("webrtc.videopeer", "autostart_failed", new { room, error = ex.Message });
                return (false, false, null, ex.Message);
            }
        }
    }

    /// <summary>
    /// Stops a helper process for a specific room.
    /// </summary>
    public (bool ok, bool stopped, string? error) StopRoom(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return (false, false, "room_required");
        }

        lock (_lock)
        {
            if (!_procs.TryGetValue(room, out var st))
            {
                return (true, false, null);
            }

            try
            {
                if (!st.Process.HasExited)
                {
                    st.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                return (false, false, ex.Message);
            }
            finally
            {
                try { st.Process.Dispose(); } catch { }
                _procs.Remove(room);
            }

            return (true, true, null);
        }
    }

    /// <summary>
    /// Returns the list of helper rooms currently running.
    /// </summary>
    public IReadOnlyList<object> GetHelpersSnapshot()
    {
        lock (_lock)
        {
            var list = new List<object>(_procs.Count);
            foreach (var kvp in _procs)
            {
                var p = kvp.Value.Process;
                list.Add(new
                {
                    room = kvp.Key,
                    pid = p.HasExited ? (int?)null : p.Id,
                    hasExited = p.HasExited,
                    startedAtUtc = kvp.Value.StartedAtUtc,
                    idleSinceUtc = kvp.Value.IdleSinceUtc
                });
            }
            return list;
        }
    }

    private void CleanupTick()
    {
        int idleStop = _opt.WebRtcRoomIdleStopSeconds;
        if (idleStop <= 0)
        {
            return;
        }
        const int startupGraceSeconds = 20;

        // "video" should be stable; don't auto-stop it.
        var peers = _signaling.GetRoomPeerCountsSnapshot();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            foreach (var room in _procs.Keys.ToArray())
            {
                if (string.Equals(room, "video", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_procs.TryGetValue(room, out var st))
                {
                    continue;
                }

                var proc = st.Process;
                if (proc.HasExited)
                {
                    try { proc.Dispose(); } catch { }
                    _procs.Remove(room);
                    continue;
                }

                // Give newly started helpers time to complete signaling/join before idle auto-stop kicks in.
                if ((now - st.StartedAtUtc).TotalSeconds < startupGraceSeconds)
                {
                    continue;
                }

                int count = peers.TryGetValue(room, out int c) ? c : 0;
                if (count <= 1)
                {
                    if (st.IdleSinceUtc is null)
                    {
                        _procs[room] = st with { IdleSinceUtc = now };
                        continue;
                    }

                    if ((now - st.IdleSinceUtc.Value).TotalSeconds >= idleStop)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        try { proc.Dispose(); } catch { }
                        _procs.Remove(room);
                        ServerEventLog.Log("webrtc.videopeer", "autostop_idle", new { room, idleStopSeconds = idleStop });
                    }
                }
                else
                {
                    if (st.IdleSinceUtc is not null)
                    {
                        _procs[room] = st with { IdleSinceUtc = null };
                    }
                }
            }
        }
    }

    private static bool TryFindToolDir(out string toolDir, out string hint)
    {
        string baseDir = AppContext.BaseDirectory;
        string? d = baseDir;
        for (int i = 0; i < 8 && d is not null; i++)
        {
            string candidate = Path.Combine(d, "Tools", "WebRtcVideoPeer");
            if (Directory.Exists(candidate))
            {
                toolDir = candidate;
                hint = candidate;
                return true;
            }
            d = Path.GetDirectoryName(d);
        }
        toolDir = string.Empty;
        hint = $"searched from {baseDir}";
        return false;
    }

    private static string BuildLocalServerUrl(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl)) return "http://127.0.0.1:8080";
        // Server itself always runs local; helper must reach it via localhost.
        // Replace host with 127.0.0.1 but keep scheme/port.
        try
        {
            var u = new Uri(configuredUrl);
            var b = new UriBuilder(u) { Host = "127.0.0.1" };
            return b.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return "http://127.0.0.1:8080";
        }
    }

    private static ProcessStartInfo? BuildStartInfo(string toolDir, string serverUrl, string room, string token, string stun)
    {
        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.Combine(toolDir, "run.ps1");
            if (!File.Exists(ps1))
            {
                return null;
            }

            // Prefer Windows PowerShell; fall back to `powershell` on PATH.
            string shell = File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "powershell";

            // run.ps1 sets env vars and calls `go run .`.
            string args = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -ServerUrl \"{serverUrl}\" -Room \"{room}\" -Token \"{token}\"";
            var psi = new ProcessStartInfo(shell, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = toolDir
            };
            psi.Environment["HIDBRIDGE_STUN"] = stun;
            return psi;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            string script = Path.Combine(toolDir, "run.sh");
            if (!File.Exists(script))
            {
                return null;
            }

            // Keep arg order consistent with WebRtcControlPeer: serverUrl, token, room, stun.
            string args = $"\"{script}\" \"{serverUrl}\" \"{token}\" \"{room}\" \"{stun}\"";
            return new ProcessStartInfo("bash", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = toolDir
            };
        }

        return null;
    }

    private static string BuildHelperLogPath(string toolDir, string room)
    {
        string safe = new string(room.Select(ch =>
            (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') ? ch : '_').ToArray());
        return Path.Combine(toolDir, "logs", $"videopeer_{safe}.log");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanupTimer.Dispose();
        Stop();
    }
}
