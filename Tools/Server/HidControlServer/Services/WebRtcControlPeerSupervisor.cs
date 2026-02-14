using System.Diagnostics;
using HidControl.Application.Abstractions;

namespace HidControlServer.Services;

/// <summary>
/// Supervises the external <c>Tools/WebRtcControlPeer</c> helper process.
/// </summary>
public sealed class WebRtcControlPeerSupervisor : IDisposable
{
    // Give helper enough time to fail fast on missing deps/toolchain issues.
    // 300ms is too optimistic on some Windows/Go setups.
    private const int EarlyExitProbeMs = 1500;
    private const int RestartMaxAttempts = 6;
    private const int RestartBaseDelayMs = 1000;
    private const int RestartMaxDelayMs = 30000;
    private const int RestartFailureWindowSeconds = 180;

    private readonly Options _opt;
    private readonly IWebRtcSignalingService _signaling;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProcState> _procs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualStopRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RestartState> _restartByRoom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _cleanupTimer;

    private sealed record ProcState(Process Process, DateTimeOffset StartedAtUtc, DateTimeOffset? IdleSinceUtc);
    private sealed record RestartState(int Attempts, DateTimeOffset LastFailureAtUtc);

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="opt">Server options.</param>
    /// <param name="signaling">Signaling room state service.</param>
    public WebRtcControlPeerSupervisor(Options opt, IWebRtcSignalingService signaling)
    {
        _opt = opt;
        _signaling = signaling;
        CleanupOrphansFromPidFiles();
        _cleanupTimer = new Timer(_ => CleanupTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(Math.Max(1, _opt.WebRtcRoomsCleanupIntervalSeconds)));
    }

    /// <summary>
    /// Starts the helper if enabled by config.
    /// </summary>
    public void StartIfEnabled()
    {
        if (!_opt.WebRtcControlPeerAutoStart)
        {
            return;
        }

        string room = string.IsNullOrWhiteSpace(_opt.WebRtcControlPeerRoom) ? "control" : _opt.WebRtcControlPeerRoom;
        EnsureStarted(room);
    }

    /// <summary>
    /// Stops the helper process if running.
    /// </summary>
    public void Stop()
    {
        string? toolDir = null;
        _ = TryFindToolDir(out toolDir!, out _);
        lock (_lock)
        {
            foreach (var kvp in _procs.ToArray())
            {
                try
                {
                    _manualStopRooms.Add(kvp.Key);
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
                    _restartByRoom.Remove(kvp.Key);
                    if (!string.IsNullOrWhiteSpace(toolDir))
                    {
                        DeletePidFile(toolDir!, kvp.Key);
                    }
                }
            }
        }
        CleanupOrphansFromPidFiles();
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
            _manualStopRooms.Remove(room);
            int max = _opt.WebRtcRoomsMaxHelpers;
            if (max > 0 && !_procs.ContainsKey(room) && _procs.Count >= max)
            {
                return (false, false, null, "max_helpers_reached");
            }

            if (_procs.TryGetValue(room, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    _restartByRoom.Remove(room);
                    return (true, false, existing.Process.Id, null);
                }

                try { existing.Process.Dispose(); } catch { }
                _procs.Remove(room);
                _restartByRoom.Remove(room);
            }

            if (!TryFindToolDir(out string toolDir, out string toolHint))
            {
                ServerEventLog.Log("webrtc.peer", "autostart_skipped", new { reason = "tool_not_found", hint = toolHint });
                return (false, false, null, "tool_not_found");
            }

            if (TryGetRunningPidFromFile(BuildPidFilePath(toolDir, room), out int existingPid))
            {
                return (true, false, existingPid, null);
            }

            string serverUrl = BuildLocalServerUrl(_opt.Url);
            string token = _opt.Token ?? string.Empty;

            var psi = BuildStartInfo(toolDir, serverUrl, room, token, _opt.WebRtcControlPeerStun);
            if (psi is null)
            {
                ServerEventLog.Log("webrtc.peer", "autostart_skipped", new { reason = "unsupported_os", os = Environment.OSVersion.Platform.ToString() });
                return (false, false, null, "unsupported_os");
            }
            string pidPath = BuildPidFilePath(toolDir, room);
            psi.Environment["HIDBRIDGE_HELPER_PIDFILE"] = pidPath;

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (_, __) =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    ServerEventLog.Log("webrtc.peer", "exit", new { room, code });
                    DeletePidFile(toolDir, room);
                    bool scheduleRestart = false;

                    lock (_lock)
                    {
                        if (_procs.TryGetValue(room, out var st) && ReferenceEquals(st.Process, p))
                        {
                            _procs.Remove(room);
                        }
                        scheduleRestart = ShouldScheduleRestart_NoLock(room, code);
                    }

                    if (scheduleRestart)
                    {
                        TryScheduleRestart(room, code);
                    }
                };

                if (!p.Start())
                {
                    ServerEventLog.Log("webrtc.peer", "autostart_failed", new { room, reason = "start_returned_false" });
                    return (false, false, null, "start_failed");
                }

                // Fail fast if helper exits immediately (missing deps, broken toolchain, bad args).
                if (p.WaitForExit(EarlyExitProbeMs))
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    if (code == 0)
                    {
                        if (TryGetRunningPidFromFile(pidPath, out int livePid))
                        {
                            ServerEventLog.Log("webrtc.peer", "autostart_early_exit_zero", new { room, code, probeMs = EarlyExitProbeMs, pid = livePid });
                            try { p.Dispose(); } catch { }
                            return (true, false, livePid, null);
                        }

                        ServerEventLog.Log("webrtc.peer", "autostart_failed", new { room, reason = "exited_early", code, probeMs = EarlyExitProbeMs });
                        try { p.Dispose(); } catch { }
                        return (false, false, null, "exit_0");
                    }

                    ServerEventLog.Log("webrtc.peer", "autostart_failed", new { room, reason = "exited_early", code, probeMs = EarlyExitProbeMs });
                    try { p.Dispose(); } catch { }
                    lock (_lock)
                    {
                        if (ShouldScheduleRestart_NoLock(room, code))
                        {
                            _ = Task.Run(() => TryScheduleRestart(room, code));
                        }
                    }
                    return (false, false, null, $"exit_{code}");
                }

                _procs[room] = new ProcState(p, DateTimeOffset.UtcNow, IdleSinceUtc: null);
                _restartByRoom.Remove(room);
                ServerEventLog.Log("webrtc.peer", "autostart_started", new
                {
                    serverUrl,
                    room,
                    stun = _opt.WebRtcControlPeerStun,
                    pid = p.Id,
                    exe = psi.FileName,
                    args = psi.Arguments,
                    pidPath
                });
                return (true, true, p.Id, null);
            }
            catch (Exception ex)
            {
                ServerEventLog.Log("webrtc.peer", "autostart_failed", new { room, error = ex.Message });
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

        string toolDir = string.Empty;
        bool hasToolDir = TryFindToolDir(out toolDir, out _);
        bool stoppedByPid = false;
        if (hasToolDir)
        {
            string pidPath = BuildPidFilePath(toolDir, room);
            if (TryGetRunningPidFromFile(pidPath, out int pid) && TryKillByPid(pid))
            {
                stoppedByPid = true;
            }
            DeletePidFile(toolDir, room);
        }

        ProcState? tracked = null;
        bool lockTaken = false;
        try
        {
            if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(5)))
            {
                // Avoid long API hangs when helper startup/cleanup currently holds the lock.
                return (true, stoppedByPid, null);
            }
            lockTaken = true;
            _manualStopRooms.Add(room);
            _restartByRoom.Remove(room);

            if (!_procs.TryGetValue(room, out tracked))
            {
                return (true, stoppedByPid, null);
            }

            _procs.Remove(room);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_lock);
            }
        }

        if (tracked is null)
        {
            return (true, stoppedByPid, null);
        }

        bool stoppedTracked = false;
        string? killError = null;
        try
        {
            if (!tracked.Process.HasExited)
            {
                tracked.Process.Kill(entireProcessTree: true);
                stoppedTracked = true;
            }
            else
            {
                stoppedTracked = true;
            }
        }
        catch (Exception ex)
        {
            killError = ex.Message;
        }
        finally
        {
            try { tracked.Process.Dispose(); } catch { }
            if (hasToolDir)
            {
                DeletePidFile(toolDir, room);
            }
        }

        if (!stoppedTracked && !stoppedByPid && !string.IsNullOrWhiteSpace(killError))
        {
            return (false, false, killError);
        }

        return (true, stoppedTracked || stoppedByPid, null);
    }

    private bool ShouldScheduleRestart_NoLock(string room, int exitCode)
    {
        if (exitCode == 0)
        {
            return false;
        }
        if (!_opt.WebRtcControlPeerAutoStart)
        {
            return false;
        }
        if (_manualStopRooms.Contains(room))
        {
            _manualStopRooms.Remove(room);
            return false;
        }
        return true;
    }

    private static int GetRestartDelayMs(int attempts)
    {
        int capped = Math.Max(1, Math.Min(16, attempts));
        long d = RestartBaseDelayMs * (1L << (capped - 1));
        if (d > RestartMaxDelayMs) d = RestartMaxDelayMs;
        return (int)d;
    }

    private void TryScheduleRestart(string room, int exitCode)
    {
        int attempts;
        int delayMs;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_restartByRoom.TryGetValue(room, out var prev) &&
                (now - prev.LastFailureAtUtc).TotalSeconds <= RestartFailureWindowSeconds)
            {
                attempts = prev.Attempts + 1;
            }
            else
            {
                attempts = 1;
            }
            _restartByRoom[room] = new RestartState(attempts, now);
            delayMs = GetRestartDelayMs(attempts);
        }

        if (attempts > RestartMaxAttempts)
        {
            ServerEventLog.Log("webrtc.peer", "autorestart_limit", new
            {
                room,
                exitCode,
                attempts,
                maxAttempts = RestartMaxAttempts
            });
            return;
        }

        ServerEventLog.Log("webrtc.peer", "autorestart_scheduled", new
        {
            room,
            exitCode,
            attempts,
            delayMs
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_procs.ContainsKey(room))
                    {
                        return;
                    }
                    if (_manualStopRooms.Contains(room))
                    {
                        _manualStopRooms.Remove(room);
                        return;
                    }
                }

                var started = EnsureStarted(room);
                if (started.ok)
                {
                    lock (_lock)
                    {
                        _restartByRoom.Remove(room);
                    }
                    ServerEventLog.Log("webrtc.peer", "autorestart_ok", new
                    {
                        room,
                        started = started.started,
                        pid = started.pid
                    });
                }
                else
                {
                    ServerEventLog.Log("webrtc.peer", "autorestart_failed", new
                    {
                        room,
                        error = started.error
                    });
                }
            }
            catch (Exception ex)
            {
                ServerEventLog.Log("webrtc.peer", "autorestart_error", new { room, error = ex.Message });
            }
        });
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

        // "control" should be stable; don't auto-stop it.
        var peers = _signaling.GetRoomPeerCountsSnapshot();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            foreach (var room in _procs.Keys.ToArray())
            {
                if (string.Equals(room, "control", StringComparison.OrdinalIgnoreCase))
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

                int count = peers.TryGetValue(room, out int c) ? c : 0;
                if (count <= 1)
                {
                    // Idle: only helper is present (or room is missing).
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
                        ServerEventLog.Log("webrtc.peer", "autostop_idle", new { room, peers = count, idleSeconds = idleStop });
                    }
                }
                else
                {
                    // Active: reset idle timer.
                    if (st.IdleSinceUtc is not null)
                    {
                        _procs[room] = st with { IdleSinceUtc = null };
                    }
                }
            }
        }
    }

    private static string BuildLocalServerUrl(string configuredUrl)
    {
        // The helper runs on the same machine; always prefer loopback for stability.
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            int port = uri.Port;
            string scheme = uri.Scheme;
            if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "http";
            }
            return $"{scheme}://127.0.0.1:{port}";
        }

        return "http://127.0.0.1:8080";
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

            // Prefer Windows PowerShell; fall back to pwsh if needed (handled by PATH).
            string shell = File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "powershell";

            // run.ps1 already sets env vars and calls `go run .`.
            string args = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -ServerUrl \"{serverUrl}\" -Room \"{room}\" -Token \"{token}\"";

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = args,
                WorkingDirectory = toolDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["HIDBRIDGE_STUN"] = stun;
            return psi;
        }

        // Linux/RPi: prefer run.sh (keeps command line stable).
        string sh = Path.Combine(toolDir, "run.sh");
        if (!File.Exists(sh))
        {
            return null;
        }

        var psiLinux = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"\"{sh}\" \"{serverUrl}\" \"{token}\" \"{room}\" \"{stun}\"",
            WorkingDirectory = toolDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return psiLinux;
    }

    private static bool TryFindToolDir(out string toolDir, out string hint)
    {
        hint = string.Empty;
        toolDir = string.Empty;

        // Search upwards for `Tools/WebRtcControlPeer`.
        string start = Directory.GetCurrentDirectory();
        var cur = new DirectoryInfo(start);
        for (int i = 0; i < 8 && cur is not null; i++)
        {
            string candidate = Path.Combine(cur.FullName, "Tools", "WebRtcControlPeer");
            if (Directory.Exists(candidate))
            {
                toolDir = candidate;
                return true;
            }
            cur = cur.Parent;
        }

        hint = $"searched up from '{start}' for 'Tools/WebRtcControlPeer'";
        return false;
    }

    private static string BuildPidFilePath(string toolDir, string room)
    {
        string safe = new string(room.Select(ch =>
            (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') ? ch : '_').ToArray());
        return Path.Combine(toolDir, "pids", $"controlpeer_{safe}.pid");
    }

    private static bool TryGetRunningPidFromFile(string pidFilePath, out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(pidFilePath))
            {
                return false;
            }

            string raw = File.ReadAllText(pidFilePath).Trim();
            if (!int.TryParse(raw, out pid) || pid <= 0)
            {
                return false;
            }

            Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryKillByPid(int pid)
    {
        try
        {
            Process p = Process.GetProcessById(pid);
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeletePidFile(string toolDir, string room)
    {
        try
        {
            string pidPath = BuildPidFilePath(toolDir, room);
            if (File.Exists(pidPath))
            {
                File.Delete(pidPath);
            }
        }
        catch { }
    }

    /// <summary>
    /// Best-effort orphan cleanup for helper PIDs persisted on disk.
    /// </summary>
    private void CleanupOrphansFromPidFiles()
    {
        if (!TryFindToolDir(out string toolDir, out _))
        {
            return;
        }

        string pidsDir = Path.Combine(toolDir, "pids");
        if (!Directory.Exists(pidsDir))
        {
            return;
        }

        int removedFiles = 0;
        int killed = 0;
        foreach (string file in Directory.EnumerateFiles(pidsDir, "controlpeer_*.pid"))
        {
            try
            {
                string raw = File.ReadAllText(file).Trim();
                if (int.TryParse(raw, out int pid) && pid > 0 && TryKillByPid(pid))
                {
                    killed++;
                }
            }
            catch { }
            finally
            {
                try
                {
                    File.Delete(file);
                    removedFiles++;
                }
                catch { }
            }
        }

        int killedByName = KillLegacyByProcessNames("webrtccontrolpeer", "WebRtcControlPeer");
        if (removedFiles > 0 || killed > 0 || killedByName > 0)
        {
            ServerEventLog.Log("webrtc.peer", "orphan_cleanup", new { removedFiles, killed, killedByName });
        }
    }

    private static int KillLegacyByProcessNames(params string[] names)
    {
        int killed = 0;
        foreach (string name in names)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (Process p in procs)
            {
                try
                {
                    if (p.HasExited)
                    {
                        continue;
                    }

                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        return killed;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try { _cleanupTimer.Dispose(); } catch { }
        Stop();
    }
}
