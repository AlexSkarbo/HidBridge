using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Implements the <c>DemoFlowCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class DemoFlowCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!DemoFlowOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"demo-flow options error: {parseError}");
            return 1;
        }

        var scriptsRoot = Path.Combine(platformRoot, "Scripts");
        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "demo-flow", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<DemoFlowStepResult>();
        var runtimeServices = new List<DemoRuntimeService>();
        var runtimeStarted = new List<DemoRuntimeService>();
        var serviceStartupAttempted = false;
        var effectiveReuse = options.ReuseRunningServices;
        var effectiveSkipCiLocal = options.SkipCiLocal;
        var effectiveSkipDemoGate = options.SkipDemoGate;

        var authUri = new Uri(options.AuthAuthority);
        var keycloakBaseUrl = authUri.IsDefaultPort
            ? $"{authUri.Scheme}://{authUri.Host}"
            : $"{authUri.Scheme}://{authUri.Host}:{authUri.Port}";
        var realmName = "hidbridge-dev";
        var path = authUri.AbsolutePath;
        if (path.StartsWith("/realms/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                realmName = parts[1];
            }
        }

        if (options.IncludeWebRtcEdgeAgentSmoke && string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowLegacyControlWs)
            {
                Console.Error.WriteLine("WebRtcCommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly.");
                return 1;
            }

            if (!TestLegacyExp022Enabled())
            {
                Console.Error.WriteLine("run_demo_flow controlws mode is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true in your shell to run exp-022/controlws lab flows.");
                return 1;
            }
            Console.WriteLine("WARNING: Legacy controlws executor enabled for demo-flow WebRTC validation (exp-022 compatibility mode).");
        }

        if (options.IncludeWebRtcEdgeAgentSmoke)
        {
            if (!effectiveReuse)
            {
                if (await TestApiHealthAsync(options.ApiBaseUrl))
                {
                    Console.WriteLine("WARNING: IncludeWebRtcEdgeAgentSmoke enabled: forcing service reuse to avoid restarting live WebRTC relay sessions.");
                    effectiveReuse = true;
                }
                else
                {
                    Console.WriteLine($"WARNING: IncludeWebRtcEdgeAgentSmoke enabled but API is not healthy at {options.ApiBaseUrl.TrimEnd('/')}/health. Starting runtime services normally (no reuse).");
                }
            }

            if (!effectiveSkipCiLocal)
            {
                Console.WriteLine("WARNING: IncludeWebRtcEdgeAgentSmoke enabled: skipping CI Local to avoid replacing the active API runtime during WebRTC relay validation.");
                effectiveSkipCiLocal = true;
            }
        }

        try
        {
            var apiUri = new Uri(options.ApiBaseUrl);
            var webUri = new Uri(options.WebBaseUrl);
            if (!effectiveReuse)
            {
                await StopPortListenersAsync(apiUri.Port, "API");
                await StopPortListenersAsync(webUri.Port, "Web");
            }

            var skipDoctorInCi = false;
            if (!options.SkipIdentityReset)
            {
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Identity Reset", "identity-reset", Array.Empty<string>());
            }

            if (!options.SkipDoctor)
            {
                var doctorRequireApi = !options.IncludeWebRtcEdgeAgentSmoke;
                var doctorStartApiProbe = !options.IncludeWebRtcEdgeAgentSmoke;
                var doctorArgs = new List<string>
                {
                    "-StartApiProbe", ToBoolLiteral(doctorStartApiProbe),
                    "-RequireApi", ToBoolLiteral(doctorRequireApi),
                    "-ApiConfiguration", options.Configuration,
                    "-ApiPersistenceProvider", "Sql",
                    "-ApiConnectionString", options.ConnectionString,
                    "-ApiSchema", options.Schema,
                    "-KeycloakBaseUrl", keycloakBaseUrl,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Doctor", "doctor", doctorArgs);
                skipDoctorInCi = true;
            }

            if (!effectiveSkipCiLocal)
            {
                var ciArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                    "-SkipDoctor", ToBoolLiteral(skipDoctorInCi),
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "CI Local", "ci-local", ciArgs);
            }

            if (options.IncludeFull)
            {
                var fullArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Full", "full", fullArgs);
            }

            if (!options.SkipServiceStartup)
            {
                serviceStartupAttempted = true;
                var apiProjectPath = Path.Combine(repoRoot, "Platform", "Platform", "HidBridge.ControlPlane.Api", "HidBridge.ControlPlane.Api.csproj");
                var webProjectPath = Path.Combine(repoRoot, "Platform", "Clients", "HidBridge.ControlPlane.Web", "HidBridge.ControlPlane.Web.csproj");

                var apiEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = options.ApiBaseUrl,
                    ["Kestrel__Endpoints__Http__Url"] = options.ApiBaseUrl,
                    ["HIDBRIDGE_PERSISTENCE_PROVIDER"] = "Sql",
                    ["HIDBRIDGE_SQL_CONNECTION"] = options.ConnectionString,
                    ["HIDBRIDGE_SQL_SCHEMA"] = options.Schema,
                    ["HIDBRIDGE_SQL_APPLY_MIGRATIONS"] = "true",
                    ["HIDBRIDGE_AUTH_ENABLED"] = "true",
                    ["HIDBRIDGE_AUTH_AUTHORITY"] = options.AuthAuthority,
                    ["HIDBRIDGE_AUTH_AUDIENCE"] = "",
                    ["HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA"] = "false",
                    ["HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES"] = "",
                    ["HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES"] = "",
                    ["HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS"] = "",
                };
                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    apiEnvironment["HIDBRIDGE_UART_PASSIVE_HEALTH_MODE"] = "true";
                    apiEnvironment["HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE"] = "true";
                    apiEnvironment["HIDBRIDGE_UART_PORT"] = "COM255";
                    apiEnvironment["HIDBRIDGE_TRANSPORT_PROVIDER"] = "webrtc-datachannel";
                    apiEnvironment["HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR"] = "false";
                    apiEnvironment["HIDBRIDGE_WEBRTC_REQUIRE_CAPABILITY"] = "false";
                    apiEnvironment["HIDBRIDGE_ENDPOINT_EXTRA_CAPABILITIES"] = "transport.webrtc.datachannel.v1@1.0";
                    // Keep relay route stable during smoke retries so command-path health
                    // is not downgraded to connector fallback between short ACK timeouts.
                    apiEnvironment["HIDBRIDGE_WEBRTC_PEER_STALE_AFTER_SEC"] = "90";
                }

                var apiRuntime = await StartDemoServiceAsync(
                    platformRoot, repoRoot, logRoot, results, options, "Start API", apiProjectPath,
                    options.ApiBaseUrl, new Uri(options.ApiBaseUrl).Host, new Uri(options.ApiBaseUrl).Port, "api-runtime",
                    useHealthProbe: true, effectiveReuse, apiEnvironment);
                runtimeServices.Add(apiRuntime);
                if (apiRuntime.Process is not null)
                {
                    runtimeStarted.Add(apiRuntime);
                }

                var demoSeedArgs = new List<string>
                {
                    "-BaseUrl", options.ApiBaseUrl,
                    "-KeycloakBaseUrl", keycloakBaseUrl,
                    "-RealmName", realmName,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Demo Seed", "demo-seed", demoSeedArgs);

                var webrtcSessionEnvPath = Path.Combine(platformRoot, ".logs", "webrtc-peer-adapter.session.env");
                var shouldBootstrapWebRtcStack = false;
                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    if (!effectiveReuse)
                    {
                        shouldBootstrapWebRtcStack = true;
                    }
                    else if (string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!await TestControlHealthAsync(options.WebRtcControlHealthUrl, Math.Max(1, options.WebRtcControlHealthAttempts), 500))
                        {
                            shouldBootstrapWebRtcStack = true;
                        }
                    }
                    else if (!File.Exists(webrtcSessionEnvPath))
                    {
                        shouldBootstrapWebRtcStack = true;
                    }
                }

                if (shouldBootstrapWebRtcStack)
                {
                    Console.WriteLine("WARNING: Bootstrapping WebRTC edge stack automatically for demo-flow WebRTC gate.");
                    var bootstrapArgs = new List<string>
                    {
                        "-ApiBaseUrl", options.ApiBaseUrl,
                        "-WebBaseUrl", options.WebBaseUrl,
                        "-CommandExecutor", options.WebRtcCommandExecutor,
                        "-SkipRuntimeBootstrap",
                        "-SkipIdentityReset",
                        "-SkipCiLocal",
                        "-StopExisting",
                    };
                    if (options.AllowLegacyControlWs)
                    {
                        bootstrapArgs.Add("-AllowLegacyControlWs");
                    }
                    if (string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
                    {
                        bootstrapArgs.Add("-ControlWsUrl");
                        bootstrapArgs.Add(ConvertControlHealthToWebSocketUrl(options.WebRtcControlHealthUrl));
                    }

                    await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "WebRTC Stack Bootstrap", "webrtc-stack", bootstrapArgs);
                }

                if (options.IncludeWebRtcEdgeAgentSmoke
                    && string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase)
                    && !await TestControlHealthAsync(options.WebRtcControlHealthUrl, Math.Max(1, options.WebRtcControlHealthAttempts), 500))
                {
                    throw new InvalidOperationException($"WebRTC control health endpoint is still not ready after bootstrap: {options.WebRtcControlHealthUrl}");
                }

                if (!effectiveSkipDemoGate)
                {
                    var demoGateArgs = new List<string>
                    {
                        "-BaseUrl", options.ApiBaseUrl,
                        "-KeycloakBaseUrl", keycloakBaseUrl,
                        "-RealmName", realmName,
                        "-OutputJsonPath", Path.Combine(logRoot, "demo-gate.result.json"),
                    };
                    if (options.IncludeWebRtcEdgeAgentSmoke)
                    {
                        demoGateArgs.AddRange(
                        [
                            "-TransportProvider", "webrtc-datachannel",
                            "-ControlHealthUrl", options.WebRtcControlHealthUrl,
                            "-RequestTimeoutSec", Math.Max(1, options.WebRtcRequestTimeoutSec).ToString(),
                            "-ControlHealthAttempts", Math.Max(1, options.WebRtcControlHealthAttempts).ToString(),
                            "-PrincipalId", "smoke-runner",
                        ]);
                        if (options.SkipTransportHealthCheck)
                        {
                            demoGateArgs.Add("-SkipTransportHealthCheck");
                        }
                        if (options.TransportHealthAttempts > 0)
                        {
                            demoGateArgs.Add("-TransportHealthAttempts");
                            demoGateArgs.Add(Math.Max(1, options.TransportHealthAttempts).ToString());
                        }
                        if (options.TransportHealthDelayMs > 0)
                        {
                            demoGateArgs.Add("-TransportHealthDelayMs");
                            demoGateArgs.Add(Math.Max(100, options.TransportHealthDelayMs).ToString());
                        }
                    }
                    if (options.RequireDemoGateDeviceAck)
                    {
                        demoGateArgs.Add("-RequireDeviceAck");
                        if (options.DemoGateKeyboardInterfaceSelector >= 0 && options.DemoGateKeyboardInterfaceSelector <= 255)
                        {
                            demoGateArgs.Add("-KeyboardInterfaceSelector");
                            demoGateArgs.Add(options.DemoGateKeyboardInterfaceSelector.ToString());
                        }
                    }

                    await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Demo Gate", "demo-gate", demoGateArgs);
                }

                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    if (effectiveSkipDemoGate)
                    {
                        if (options.SkipWebRtcSmokeStep)
                        {
                            Console.WriteLine("WARNING: Skipping standalone WebRTC Edge Agent Smoke step by request (-SkipWebRtcSmokeStep).");
                        }
                        else
                        {
                            var smokeArgs = new List<string>
                            {
                                "-ApiBaseUrl", options.ApiBaseUrl,
                                "-KeycloakBaseUrl", keycloakBaseUrl,
                                "-RealmName", realmName,
                                "-TokenScope", "openid",
                                "-ControlHealthUrl", options.WebRtcControlHealthUrl,
                                "-RequestTimeoutSec", Math.Max(1, options.WebRtcRequestTimeoutSec).ToString(),
                                "-ControlHealthAttempts", Math.Max(1, options.WebRtcControlHealthAttempts).ToString(),
                                "-SkipTransportHealthCheck", ToBoolLiteral(options.SkipTransportHealthCheck),
                                "-TransportHealthAttempts", (options.TransportHealthAttempts > 0 ? Math.Max(1, options.TransportHealthAttempts) : 20).ToString(),
                                "-TransportHealthDelayMs", (options.TransportHealthDelayMs > 0 ? Math.Max(100, options.TransportHealthDelayMs) : 500).ToString(),
                                "-OutputJsonPath", Path.Combine(logRoot, "webrtc-edge-agent-smoke.result.json"),
                            };
                            await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "WebRTC Edge Agent Smoke", "webrtc-edge-agent-smoke", smokeArgs);
                        }
                    }
                    else
                    {
                        Console.WriteLine("WARNING: Skipping standalone WebRTC Edge Agent Smoke step because Demo Gate already executed WebRTC transport validation in this run.");
                    }
                }

                var webEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = options.WebBaseUrl,
                    ["Kestrel__Endpoints__Http__Url"] = options.WebBaseUrl,
                    ["ControlPlaneApi__BaseUrl"] = options.ApiBaseUrl,
                    ["ControlPlaneApi__PropagateAccessToken"] = "true",
                    ["ControlPlaneApi__PropagateIdentityHeaders"] = "true",
                    ["Identity__Enabled"] = "true",
                    ["Identity__ProviderDisplayName"] = "Keycloak",
                    ["Identity__Authority"] = options.AuthAuthority,
                    ["Identity__ClientId"] = "controlplane-web",
                    ["Identity__ClientSecret"] = "hidbridge-web-dev-secret",
                    ["Identity__RequireHttpsMetadata"] = "false",
                    ["Identity__DisablePushedAuthorization"] = "true",
                    ["Identity__PrincipalClaimType"] = "principal_id",
                    ["Identity__TenantClaimType"] = "tenant_id",
                    ["Identity__OrganizationClaimType"] = "org_id",
                    ["Identity__RoleClaimType"] = "role",
                };
                if (options.EnableCallerContextScope)
                {
                    webEnvironment["Identity__Scopes__0"] = "hidbridge-caller-context-v2";
                }

                var webRuntime = await StartDemoServiceAsync(
                    platformRoot, repoRoot, logRoot, results, options, "Start Web", webProjectPath,
                    options.WebBaseUrl, new Uri(options.WebBaseUrl).Host, new Uri(options.WebBaseUrl).Port, "web-runtime",
                    useHealthProbe: false, effectiveReuse, webEnvironment);
                runtimeServices.Add(webRuntime);
                if (webRuntime.Process is not null)
                {
                    runtimeStarted.Add(webRuntime);
                }
            }
        }
        catch (Exception ex)
        {
            var abortLogPath = Path.Combine(logRoot, "demo-flow.abort.log");
            await File.AppendAllTextAsync(abortLogPath, ex + Environment.NewLine);
            results.Add(new DemoFlowStepResult("Demo flow orchestration", "FAIL", 0, 1, abortLogPath));
            Console.WriteLine();
            Console.WriteLine($"Demo flow aborted: {ex.Message}");
        }

        WriteSummary(results, logRoot, "Demo Flow Summary");

        if (runtimeServices.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Demo endpoints:");
            foreach (var runtime in runtimeServices)
            {
                Console.WriteLine($"- {runtime.Name}: {runtime.BaseUrl}");
            }

            if (runtimeStarted.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Stop commands:");
                foreach (var runtime in runtimeStarted)
                {
                    Console.WriteLine($"- Stop-Process -Id {runtime.Process!.Id}");
                }
            }
        }
        else if (serviceStartupAttempted)
        {
            Console.WriteLine();
            Console.WriteLine("Runtime services were already running; no new processes were started.");
        }

        return results.Any(static r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static async Task RunRuntimeCtlStepAsync(
        string platformRoot,
        string logRoot,
        List<DemoFlowStepResult> results,
        string name,
        string command,
        IReadOnlyList<string> commandArgs)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
        var stopwatch = Stopwatch.StartNew();
        var logPath = Path.Combine(logRoot, $"{NormalizeName(name)}.log");

        var runtimeCtlProject = Path.Combine(platformRoot, "Tools", "HidBridge.RuntimeCtl", "HidBridge.RuntimeCtl.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = platformRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(runtimeCtlProject);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--platform-root");
        startInfo.ArgumentList.Add(platformRoot);
        startInfo.ArgumentList.Add(command);
        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            results.Add(new DemoFlowStepResult(name, "FAIL", stopwatch.Elapsed.TotalSeconds, 1, logPath));
            throw new InvalidOperationException($"Failed to start RuntimeCtl command '{command}'.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        stopwatch.Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? platformRoot);
        await File.WriteAllTextAsync(logPath, $"{stdOut}{Environment.NewLine}{stdErr}".Trim());

        if (process.ExitCode != 0)
        {
            results.Add(new DemoFlowStepResult(name, "FAIL", stopwatch.Elapsed.TotalSeconds, process.ExitCode, logPath));
            Console.WriteLine($"FAIL  {name}");
            Console.WriteLine($"Log:   {logPath}");
            throw new InvalidOperationException($"{name} failed with exit code {process.ExitCode}. See log: {logPath}");
        }

        results.Add(new DemoFlowStepResult(name, "PASS", stopwatch.Elapsed.TotalSeconds, 0, logPath));
        Console.WriteLine($"PASS  {name}");
        Console.WriteLine($"Log:   {logPath}");
    }

    private static async Task<DemoRuntimeService> StartDemoServiceAsync(
        string platformRoot,
        string repoRoot,
        string logRoot,
        List<DemoFlowStepResult> results,
        DemoFlowOptions options,
        string name,
        string projectPath,
        string baseUrl,
        string targetHost,
        int port,
        string category,
        bool useHealthProbe,
        bool reuseRunningService,
        IReadOnlyDictionary<string, string> environment)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
        var stdoutLog = Path.Combine(logRoot, $"{category}.stdout.log");
        var stderrLog = Path.Combine(logRoot, $"{category}.stderr.log");
        EnsureEmptyFile(stdoutLog);
        EnsureEmptyFile(stderrLog);

        var sw = Stopwatch.StartNew();
        if (await TestTcpPortAsync(targetHost, port))
        {
            if (reuseRunningService)
            {
                if (useHealthProbe && !await TestApiHealthAsync(baseUrl))
                {
                    throw new InvalidOperationException($"{name} reuse requested but health probe failed at {baseUrl.TrimEnd('/')}/health. Ensure runtime is running and healthy, or run without -ReuseRunningServices.");
                }

                sw.Stop();
                results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
                Console.WriteLine($"PASS  {name}");
                Console.WriteLine($"URL:   {baseUrl} (already running)");
                return new DemoRuntimeService(name, baseUrl, Started: false, AlreadyRunning: true, Process: null, stdoutLog, stderrLog);
            }

            await StopPortListenersAsync(port, name);
            await Task.Delay(500);
            if (await TestTcpPortAsync(targetHost, port))
            {
                if (useHealthProbe && await TestApiHealthAsync(baseUrl))
                {
                    sw.Stop();
                    results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
                    Console.WriteLine($"PASS  {name}");
                    Console.WriteLine($"URL:   {baseUrl} (already running)");
                    return new DemoRuntimeService(name, baseUrl, Started: false, AlreadyRunning: true, Process: null, stdoutLog, stderrLog);
                }

                throw new InvalidOperationException($"{name} could not claim port {port} because another process is still listening.");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.Configuration);
        startInfo.ArgumentList.Add("--no-launch-profile");
        if (options.NoBuild)
        {
            startInfo.ArgumentList.Add("--no-build");
        }
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        var process = WebRtcStackCommand.StartRedirectedProcess(startInfo, stdoutLog, stderrLog);
        var started = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500);
            if (useHealthProbe)
            {
                if (await TestApiHealthAsync(baseUrl))
                {
                    started = true;
                    break;
                }
            }
            else
            {
                if (await TestTcpPortAsync(targetHost, port))
                {
                    started = true;
                    break;
                }
            }

            if (process.HasExited)
            {
                break;
            }
        }

        sw.Stop();
        if (started)
        {
            results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
            Console.WriteLine($"PASS  {name}");
            Console.WriteLine($"URL:   {baseUrl}");
            Console.WriteLine($"PID:   {process.Id}");
            Console.WriteLine($"Out:   {stdoutLog}");
            Console.WriteLine($"Err:   {stderrLog}");
            return new DemoRuntimeService(name, baseUrl, Started: true, AlreadyRunning: false, process, stdoutLog, stderrLog);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore cleanup errors
        }

        results.Add(new DemoFlowStepResult(name, "FAIL", sw.Elapsed.TotalSeconds, 1, stderrLog));
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"Out:   {stdoutLog}");
        Console.WriteLine($"Err:   {stderrLog}");
        throw new InvalidOperationException($"{name} failed to start. See logs: {stdoutLog} / {stderrLog}");
    }

    private static async Task<bool> TestApiHealthAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return json.RootElement.TryGetProperty("status", out var status)
                   && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TestControlHealthAsync(string url, int attempts, int delayMs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var json = await JsonDocument.ParseAsync(stream);
                    if (json.RootElement.TryGetProperty("ok", out var okProp)
                        && (okProp.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    {
                        if (okProp.GetBoolean())
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            await Task.Delay(Math.Max(100, delayMs));
        }

        return false;
    }

    private static async Task<bool> TestTcpPortAsync(string host, int port, int timeoutMs = 1500)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed == timeoutTask)
            {
                return false;
            }
            await connectTask;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopPortListenersAsync(int port, string label)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = $$"""
            $port = {{port}}
            $label = '{{label}}'
            $listeners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
            foreach ($listenerPid in $listeners) {
                if (-not $listenerPid -or $listenerPid -le 0) { continue }
                try {
                    $existingProcess = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
                    if ($null -eq $existingProcess) {
                        Write-Warning "Preflight cleanup ($label): PID $listenerPid already stopped before port $port cleanup."
                        continue
                    }
                    Stop-Process -Id $listenerPid -Force -ErrorAction Stop
                    Write-Host "Preflight cleanup ($label): stopped listener PID $listenerPid on port $port."
                } catch {
                    $existingProcess = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
                    if ($null -eq $existingProcess) {
                        Write-Warning "Preflight cleanup ($label): PID $listenerPid exited during port $port cleanup."
                        continue
                    }
                    throw "Preflight cleanup ($label) failed to stop PID $listenerPid on port $port."
                }
            }
            """;

        var (shell, prefix) = ResolvePowerShellExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var item in prefix)
        {
            psi.ArgumentList.Add(item);
        }
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return;
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.Write(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Write(stderr);
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preflight cleanup ({label}) failed for port {port}.");
        }
    }

    private static (string Shell, IReadOnlyList<string> PrefixArgs) ResolvePowerShellExecutable()
    {
        static string? FindInPath(IReadOnlyList<string> names)
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

        if (OperatingSystem.IsWindows())
        {
            var shell = FindInPath(["powershell.exe", "pwsh.exe"])
                ?? throw new InvalidOperationException("Unable to locate powershell.exe or pwsh.exe in PATH.");
            return (shell, ["-NoProfile", "-ExecutionPolicy", "Bypass"]);
        }

        var unixShell = FindInPath(["pwsh"])
            ?? throw new InvalidOperationException("Unable to locate pwsh in PATH.");
        return (unixShell, ["-NoProfile"]);
    }

    private static string ConvertControlHealthToWebSocketUrl(string controlHealthUrl)
    {
        var uri = new Uri(controlHealthUrl);
        var builder = new UriBuilder(uri)
        {
            Scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = uri.AbsolutePath.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsolutePath[..^"/health".Length] + "/ws/control"
                : "/ws/control",
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool TestLegacyExp022Enabled()
    {
        foreach (var candidate in ReadLegacyEnvCandidates("HIDBRIDGE_ENABLE_LEGACY_EXP022"))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }
            switch (candidate.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<string?> ReadLegacyEnvCandidates(string name)
    {
        yield return Environment.GetEnvironmentVariable(name);
        if (OperatingSystem.IsWindows())
        {
            string? user = null;
            string? machine = null;
            try { user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); } catch { }
            try { machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine); } catch { }
            yield return user;
            yield return machine;
        }
    }

    private static string ToBoolLiteral(bool value) => value ? "true" : "false";

    private static string NormalizeName(string name)
        => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-'))
            .Trim('-');

    private static void WriteSummary(IReadOnlyList<DemoFlowStepResult> results, string logRoot, string header)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {header} ===");
        Console.WriteLine();
        Console.WriteLine($"{"Name",-28} {"Status",-6} {"Seconds",7} {"ExitCode",8}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name,-28} {result.Status,-6} {result.Seconds,7:F2} {result.ExitCode,8}");
        }
        Console.WriteLine();
        Console.WriteLine($"Logs root: {logRoot}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name}: {result.LogPath}");
        }
    }

    private static void EnsureEmptyFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(path, string.Empty);
    }

    private sealed record DemoFlowStepResult(string Name, string Status, double Seconds, int ExitCode, string LogPath);

    private sealed record DemoRuntimeService(
        string Name,
        string BaseUrl,
        bool Started,
        bool AlreadyRunning,
        Process? Process,
        string StdoutLog,
        string StderrLog);

    private sealed class DemoFlowOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string AuthAuthority { get; set; } = "http://127.0.0.1:18096/realms/hidbridge-dev";
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string WebBaseUrl { get; set; } = "http://127.0.0.1:18110";
        public bool SkipIdentityReset { get; set; }
        public bool SkipDoctor { get; set; }
        public bool SkipCiLocal { get; set; }
        public bool IncludeFull { get; set; }
        public bool SkipServiceStartup { get; set; }
        public bool SkipDemoGate { get; set; }
        public bool NoBuild { get; set; }
        public bool ShowServiceWindows { get; set; }
        public bool ReuseRunningServices { get; set; }
        public bool EnableCallerContextScope { get; set; }
        public bool RequireDemoGateDeviceAck { get; set; }
        public int DemoGateKeyboardInterfaceSelector { get; set; } = -1;
        public bool IncludeWebRtcEdgeAgentSmoke { get; set; }
        public bool SkipWebRtcSmokeStep { get; set; }
        public string WebRtcCommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string WebRtcControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public int WebRtcRequestTimeoutSec { get; set; } = 15;
        public int WebRtcControlHealthAttempts { get; set; } = 20;
        public bool SkipTransportHealthCheck { get; set; }
        public int TransportHealthAttempts { get; set; } = -1;
        public int TransportHealthDelayMs { get; set; } = -1;

        public static bool TryParse(IReadOnlyList<string> args, out DemoFlowOptions options, out string? error)
        {
            options = new DemoFlowOptions();
            error = null;

            for (var i = 0; i < args.Count; i++)
            {
                var token = args[i];
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (!token.StartsWith("-", StringComparison.Ordinal))
                {
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option.";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                var value = hasValue ? args[i + 1] : null;
                switch (name.ToLowerInvariant())
                {
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "connectionstring": options.ConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "schema": options.Schema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "apibaseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webbaseurl": options.WebBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipidentityreset": options.SkipIdentityReset = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipdoctor": options.SkipDoctor = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipcilocal": options.SkipCiLocal = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includefull": options.IncludeFull = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipservicestartup": options.SkipServiceStartup = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipdemogate": options.SkipDemoGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "nobuild": options.NoBuild = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "showservicewindows": options.ShowServiceWindows = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "reuserunningservices": options.ReuseRunningServices = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "enablecallercontextscope": options.EnableCallerContextScope = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "requiredemogatedeviceack": options.RequireDemoGateDeviceAck = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "demogatekeyboardinterfaceselector": options.DemoGateKeyboardInterfaceSelector = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcedgeagentsmoke": options.IncludeWebRtcEdgeAgentSmoke = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipwebrtcsmokestep": options.SkipWebRtcSmokeStep = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccommandexecutor":
                    case "webrtccommandeexecutor":
                        options.WebRtcCommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccontrolhealthurl": options.WebRtcControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtcrequesttimeoutsec": options.WebRtcRequestTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "webrtccontrolhealthattempts": options.WebRtcControlHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skiptransporthealthcheck": options.SkipTransportHealthCheck = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthattempts": options.TransportHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthdelayms": options.TransportHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported demo-flow option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (!string.Equals(options.WebRtcCommandExecutor, "uart", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
            {
                error = "WebRtcCommandExecutor must be 'uart' or 'controlws'.";
                return false;
            }

            return true;
        }

        private static string RequireValue(string name, string? value, ref int index, ref bool hasValue, ref string? error)
        {
            if (!hasValue || string.IsNullOrWhiteSpace(value))
            {
                error = $"Option -{name} requires a value.";
                return string.Empty;
            }
            index++;
            return value;
        }

        private static int ParseInt(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            var raw = RequireValue(name, value, ref index, ref hasValue, ref error);
            if (error is not null)
            {
                return 0;
            }
            if (!int.TryParse(raw, out var parsed))
            {
                error = $"Option -{name} requires an integer value.";
                return 0;
            }
            return parsed;
        }

        private static bool ParseSwitch(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            if (!hasValue)
            {
                return true;
            }
            if (!TryParseBool(value, out var parsed))
            {
                error = $"Option -{name} requires boolean value true/false when explicitly set.";
                return false;
            }
            index++;
            return parsed;
        }

        private static bool TryParseBool(string? value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    parsed = true;
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }
    }
}
