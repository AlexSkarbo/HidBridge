using System.Diagnostics;

namespace HidControlServer.Services;

/// <summary>
/// Supervises the external <c>Tools/WebRtcControlPeer</c> helper process.
/// </summary>
public sealed class WebRtcControlPeerSupervisor : IDisposable
{
    private readonly Options _opt;
    private Process? _process;
    private readonly object _lock = new();

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="opt">Server options.</param>
    public WebRtcControlPeerSupervisor(Options opt)
    {
        _opt = opt;
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

        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
            {
                return;
            }

            if (!TryFindToolDir(out string toolDir, out string toolHint))
            {
                ServerEventLog.Log("webrtc.peer", "autostart_skipped", new { reason = "tool_not_found", hint = toolHint });
                return;
            }

            string serverUrl = BuildLocalServerUrl(_opt.Url);
            string room = string.IsNullOrWhiteSpace(_opt.WebRtcControlPeerRoom) ? "control" : _opt.WebRtcControlPeerRoom;
            string token = _opt.Token ?? string.Empty;

            var psi = BuildStartInfo(toolDir, serverUrl, room, token, _opt.WebRtcControlPeerStun);
            if (psi is null)
            {
                ServerEventLog.Log("webrtc.peer", "autostart_skipped", new { reason = "unsupported_os", os = Environment.OSVersion.Platform.ToString() });
                return;
            }

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (_, __) =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    ServerEventLog.Log("webrtc.peer", "exit", new { code });
                };

                if (!p.Start())
                {
                    ServerEventLog.Log("webrtc.peer", "autostart_failed", new { reason = "start_returned_false" });
                    return;
                }

                _process = p;
                ServerEventLog.Log("webrtc.peer", "autostart_started", new
                {
                    serverUrl,
                    room,
                    stun = _opt.WebRtcControlPeerStun,
                    pid = p.Id,
                    exe = psi.FileName,
                    args = psi.Arguments
                });
            }
            catch (Exception ex)
            {
                ServerEventLog.Log("webrtc.peer", "autostart_failed", new { error = ex.Message });
            }
        }
    }

    /// <summary>
    /// Stops the helper process if running.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                try { _process.Dispose(); } catch { }
                _process = null;
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

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
    }
}

