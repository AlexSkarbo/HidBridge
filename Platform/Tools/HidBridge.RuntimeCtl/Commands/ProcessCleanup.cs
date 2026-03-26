using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// Best-effort cleanup of stale local edge/acceptance processes that can remain after interrupted runs.
/// </summary>
internal static class ProcessCleanup
{
    public static async Task CleanupStaleEdgeProcessesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = """
            $needles = @('HidBridge.Acceptance.Runner', 'HidBridge.EdgeProxy.Agent', 'exp-022-datachanneldotnet')
            function Stop-MatchingProcesses {
                param([string]$ProcessName, [string[]]$Needles)
                $processes = @(Get-CimInstance Win32_Process -Filter "Name='$ProcessName'" -ErrorAction SilentlyContinue)
                $currentPid = $PID
                foreach ($proc in $processes) {
                    if ($proc.ProcessId -eq $currentPid) { continue }
                    $line = [string]$proc.CommandLine
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $matches = $false
                    foreach ($needle in $Needles) {
                        if ($line -like "*$needle*") { $matches = $true; break }
                    }
                    if (-not $matches) { continue }
                    try {
                        Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
                        Write-Host "Stopped stale PID $($proc.ProcessId): $ProcessName"
                    } catch {
                        Write-Warning "Failed to stop stale PID $($proc.ProcessId): $($_.Exception.Message)"
                    }
                }
            }
            Stop-MatchingProcesses -ProcessName 'dotnet.exe' -Needles $needles
            Stop-MatchingProcesses -ProcessName 'pwsh.exe' -Needles $needles
            Stop-MatchingProcesses -ProcessName 'powershell.exe' -Needles $needles
            """;

        var shell = ResolvePowerShellExecutable();
        if (string.IsNullOrWhiteSpace(shell))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync();
        var exited = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(20))) == waitTask;
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
            Console.WriteLine("WARNING: Stale-process cleanup timed out after 20s and was terminated.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            Console.Write(stdOut);
        }
        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            Console.Write(stdErr);
        }
    }

    private static string? ResolvePowerShellExecutable()
    {
        static string? FindInPath(string[] names)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in names)
            {
                foreach (var entry in entries)
                {
                    var candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        return FindInPath(new[] { "pwsh.exe", "powershell.exe" });
    }
}
