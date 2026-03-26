using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var options = AcceptanceRunnerOptions.Parse(args);
var platformRoot = ResolvePlatformRoot(options.PlatformRoot);
var logsRoot = Path.Combine(platformRoot, ".logs", "webrtc-edge-agent-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(logsRoot);

var stackSummaryPath = Path.Combine(logsRoot, "webrtc-stack.summary.json");
var smokeSummaryPath = Path.Combine(logsRoot, "webrtc-edge-agent-smoke.result.json");

var result = new AcceptanceSummary
{
    ApiBaseUrl = options.ApiBaseUrl,
    CommandExecutor = options.CommandExecutor,
    StackSummaryPath = string.Empty,
    SmokeSummaryPath = smokeSummaryPath,
};

Console.WriteLine("=== WebRTC Edge Agent Acceptance (.NET runner) ===");
Console.WriteLine($"Platform root: {platformRoot}");
Console.WriteLine($"Logs root:     {logsRoot}");
StackBootstrapRuntime? bootstrapRuntime = null;
try
{
    Console.WriteLine("\n=== WebRTC Stack ===");
    bootstrapRuntime = await BootstrapWebRtcStackAsync(options, platformRoot, stackSummaryPath, CancellationToken.None);

    var sessionRuntime = ResolveSessionRuntime(options, platformRoot);
    result.Stack = TryReadJson(stackSummaryPath) ?? JsonSerializer.SerializeToElement(new
    {
        sessionId = sessionRuntime.SessionId,
        peerId = sessionRuntime.PeerId,
        endpointId = sessionRuntime.EndpointId,
        runtimeBootstrapSkipped = options.SkipRuntimeBootstrap,
        source = "dotnet-acceptance-runner",
    });
    result.StackSummaryPath = stackSummaryPath;

    Console.WriteLine("\n=== WebRTC Edge Agent Smoke ===");
    var smokeSummary = await RunSmokeAsync(options, sessionRuntime, smokeSummaryPath, CancellationToken.None);
    await WriteJsonAsync(smokeSummaryPath, smokeSummary);
    result.Smoke = TryReadJson(smokeSummaryPath);
    var commandPass = string.Equals(smokeSummary.CommandStatus, "Applied", StringComparison.OrdinalIgnoreCase);
    var mediaPass = smokeSummary.MediaGatePass ?? true;
    result.Pass = commandPass && mediaPass;
    var smokeExitCode = result.Pass ? 0 : 1;

    await WriteSummaryAsync(options.OutputJsonPath, result, platformRoot);

    Console.WriteLine("\n=== Acceptance Summary ===");
    Console.WriteLine($"Result: {(result.Pass ? "PASS" : "FAIL")}");
    Console.WriteLine($"Session:       {sessionRuntime.SessionId}");
    Console.WriteLine($"Peer:          {sessionRuntime.PeerId}");
    if (smokeSummary.MediaGateRequired)
    {
        Console.WriteLine($"Media gate:    {(smokeSummary.MediaGatePass == true ? "PASS" : "FAIL")}");
        if (!string.IsNullOrWhiteSpace(smokeSummary.MediaGateFailureReason))
        {
            Console.WriteLine($"Media reason:  {smokeSummary.MediaGateFailureReason}");
        }
    }

    Console.WriteLine($"Smoke summary: {smokeSummaryPath}");
    if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
    {
        Console.WriteLine($"Output JSON: {ResolveOutputPath(options.OutputJsonPath, platformRoot)}");
    }

    return smokeExitCode;
}
finally
{
    if (options.StopStackAfter && bootstrapRuntime is not null)
    {
        StopProcessById(bootstrapRuntime.AdapterPid, "edge-adapter");
        StopProcessById(bootstrapRuntime.Exp022Pid, "exp-022");
    }
}

static async Task<SmokeSummary> RunSmokeAsync(
    AcceptanceRunnerOptions options,
    SessionRuntime sessionRuntime,
    string smokeSummaryPath,
    CancellationToken cancellationToken)
{
    using var keycloakClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSec)),
    };
    using var apiClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSec)),
    };

    var token = await AcquireAccessTokenAsync(options, keycloakClient, cancellationToken);
    apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    apiClient.DefaultRequestHeaders.Add("X-HidBridge-UserId", options.PrincipalId);
    apiClient.DefaultRequestHeaders.Add("X-HidBridge-PrincipalId", options.PrincipalId);
    apiClient.DefaultRequestHeaders.Add("X-HidBridge-TenantId", "local-tenant");
    apiClient.DefaultRequestHeaders.Add("X-HidBridge-OrganizationId", "local-org");
    apiClient.DefaultRequestHeaders.Add("X-HidBridge-Role", "operator.admin,operator.moderator,operator.viewer");

    sessionRuntime = await EnsureControlLeaseAsync(options, apiClient, sessionRuntime, cancellationToken);
    await MarkPeerOnlineAsync(options, apiClient, sessionRuntime, cancellationToken);
    var readiness = options.SkipTransportHealthCheck
        ? null
        : await WaitRelayTransportReadyAsync(options, apiClient, sessionRuntime.SessionId, cancellationToken);

    var commandAttempts = new List<SmokeCommandAttempt>();
    var effectiveText = $"hello webrtc {DateTimeOffset.UtcNow:HH:mm:ss}";
    var maxAttempts = Math.Max(1, options.CommandAttempts);
    var retryDelay = Math.Max(100, options.CommandRetryDelayMs);
    CommandAck? finalAck = null;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        var commandId = $"cmd-webrtc-smoke-{DateTimeOffset.UtcNow:HHmmssfff}";
        finalAck = await DispatchCommandAsync(options, apiClient, sessionRuntime, commandId, effectiveText, cancellationToken);
        commandAttempts.Add(new SmokeCommandAttempt(attempt, commandId, finalAck.Status, DateTimeOffset.UtcNow));
        if (string.Equals(finalAck.Status, "Applied", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (attempt >= maxAttempts || !IsRetryable(finalAck))
        {
            break;
        }

        if (!options.SkipTransportHealthCheck)
        {
            try
            {
                readiness = await WaitRelayTransportReadyAsync(options, apiClient, sessionRuntime.SessionId, cancellationToken);
            }
            catch
            {
                // Keep command retry behavior deterministic even when readiness re-check fails.
            }
        }

        await Task.Delay(retryDelay, cancellationToken);
    }

    if (finalAck is null)
    {
        throw new InvalidOperationException("Smoke command did not produce command result.");
    }

    MediaGateResult? mediaGate = null;
    if (options.RequireMediaReady || options.RequireMediaPlaybackUrl)
    {
        mediaGate = await WaitMediaGateAsync(options, apiClient, sessionRuntime, cancellationToken);
    }

    return new SmokeSummary
    {
        ApiBaseUrl = options.ApiBaseUrl,
        SessionId = sessionRuntime.SessionId,
        PeerId = sessionRuntime.PeerId,
        PrincipalId = options.PrincipalId,
        CommandId = finalAck.CommandId,
        CommandAction = options.CommandAction,
        CommandStatus = finalAck.Status,
        CommandResponse = finalAck.Raw,
        CommandAttemptsConfigured = maxAttempts,
        CommandAttempts = commandAttempts,
        TransportReadiness = readiness,
        MediaGateRequired = options.RequireMediaReady || options.RequireMediaPlaybackUrl,
        MediaGatePass = mediaGate?.Pass,
        MediaReadyObserved = mediaGate?.ObservedMediaReady,
        MediaPlaybackUrlObserved = mediaGate?.ObservedPlaybackUrl,
        MediaGateFailureReason = mediaGate?.FailureReason,
        MediaReadiness = mediaGate?.ReadinessSnapshot,
        MediaStreams = mediaGate?.StreamsSnapshot,
        OutputPath = smokeSummaryPath,
    };
}

static SessionRuntime ResolveSessionRuntime(AcceptanceRunnerOptions options, string platformRoot)
{
    var sessionId = options.SessionId;
    var peerId = options.PeerId;
    var endpointId = options.EndpointId;
    var envPath = ResolveOutputPath(options.SessionEnvPath, platformRoot);
    if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(envPath))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            kv[line[..separator]] = line[(separator + 1)..];
        }

        if (kv.TryGetValue("SESSION_ID", out var envSession) && !string.IsNullOrWhiteSpace(envSession))
        {
            sessionId = envSession;
        }

        if (kv.TryGetValue("PEER_ID", out var envPeer) && !string.IsNullOrWhiteSpace(envPeer))
        {
            peerId = envPeer;
        }

        if (kv.TryGetValue("ENDPOINT_ID", out var envEndpoint) && !string.IsNullOrWhiteSpace(envEndpoint))
        {
            endpointId = envEndpoint;
        }
    }

    if (string.IsNullOrWhiteSpace(sessionId))
    {
        sessionId = $"room-webrtc-peer-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    if (string.IsNullOrWhiteSpace(peerId))
    {
        peerId = "peer-local-uart-edge";
    }

    if (string.IsNullOrWhiteSpace(endpointId))
    {
        endpointId = "endpoint_local_demo";
    }

    return new SessionRuntime(
        SessionId: sessionId ?? string.Empty,
        PeerId: peerId ?? string.Empty,
        EndpointId: endpointId);
}

static async Task<StackBootstrapRuntime> BootstrapWebRtcStackAsync(
    AcceptanceRunnerOptions options,
    string platformRoot,
    string stackSummaryPath,
    CancellationToken cancellationToken)
{
    var scriptPath = Path.Combine(platformRoot, "Scripts", "run_webrtc_stack.ps1");
    if (!File.Exists(scriptPath))
    {
        throw new FileNotFoundException($"WebRTC stack bootstrap script not found: {scriptPath}");
    }

    var logRoot = Path.GetDirectoryName(stackSummaryPath) ?? platformRoot;
    Directory.CreateDirectory(logRoot);
    var stdoutPath = Path.Combine(logRoot, "webrtc-stack.stdout.log");
    var stderrPath = Path.Combine(logRoot, "webrtc-stack.stderr.log");

    var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
    var startInfo = new ProcessStartInfo
    {
        FileName = shell,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };

    if (OperatingSystem.IsWindows())
    {
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
    }
    else
    {
        startInfo.ArgumentList.Add("-NoProfile");
    }

    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);
    startInfo.ArgumentList.Add("-ApiBaseUrl");
    startInfo.ArgumentList.Add(options.ApiBaseUrl);
    startInfo.ArgumentList.Add("-CommandExecutor");
    startInfo.ArgumentList.Add(options.CommandExecutor);
    startInfo.ArgumentList.Add("-OutputJsonPath");
    startInfo.ArgumentList.Add(stackSummaryPath);
    startInfo.ArgumentList.Add("-SkipIdentityReset");
    startInfo.ArgumentList.Add("-SkipCiLocal");
    startInfo.ArgumentList.Add("-TokenUsername");
    startInfo.ArgumentList.Add(options.TokenUsername);
    startInfo.ArgumentList.Add("-TokenPassword");
    startInfo.ArgumentList.Add(options.TokenPassword);
    startInfo.ArgumentList.Add("-KeycloakBaseUrl");
    startInfo.ArgumentList.Add(options.KeycloakBaseUrl);
    if (options.SkipRuntimeBootstrap)
    {
        startInfo.ArgumentList.Add("-SkipRuntimeBootstrap");
    }

    if (options.StopExisting)
    {
        startInfo.ArgumentList.Add("-StopExisting");
    }

    if (options.AllowLegacyControlWs)
    {
        startInfo.ArgumentList.Add("-AllowLegacyControlWs");
    }

    if (!string.IsNullOrWhiteSpace(options.ControlWsUrl))
    {
        startInfo.ArgumentList.Add("-ControlWsUrl");
        startInfo.ArgumentList.Add(options.ControlWsUrl);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start WebRTC stack bootstrap process.");

    var stackReady = false;
    var terminatedAfterSummary = false;
    var bootstrapTimeout = TimeSpan.FromSeconds(Math.Max(30, options.RequestTimeoutSec * 6));
    var bootstrapDeadline = DateTimeOffset.UtcNow.Add(bootstrapTimeout);
    Console.WriteLine($"Waiting for WebRTC stack summary ({bootstrapTimeout.TotalSeconds:0}s timeout)...");

    while (!process.HasExited && DateTimeOffset.UtcNow < bootstrapDeadline)
    {
        if (TryReadJson(stackSummaryPath) is not null)
        {
            stackReady = true;
            Console.WriteLine("WebRTC stack summary detected.");
            break;
        }

        await Task.Delay(250, cancellationToken);
    }
    Console.WriteLine($"WebRTC stack bootstrap state: stackReady={stackReady}, processExited={process.HasExited}.");

    var timedOutWithoutSummary = !stackReady && !process.HasExited;

    if (stackReady && !process.HasExited)
    {
        // `run_webrtc_stack.ps1` can stay alive in some host shells even after writing summary.
        // Once summary exists, terminate only the script process and keep spawned adapter alive.
        try
        {
            process.Kill(entireProcessTree: false);
            terminatedAfterSummary = true;
        }
        catch
        {
            // best effort
        }
    }
    else if (!stackReady && !process.HasExited)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch
        {
            // best effort
        }
    }

    if (!process.HasExited)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException) when (stackReady)
        {
            // Summary exists; do not block acceptance on lingering bootstrap shell process.
        }
        catch (Exception) when (stackReady)
        {
            // Summary already exists; continue.
        }
    }

    await File.WriteAllTextAsync(
        stdoutPath,
        "Bootstrap stdout is not redirected by the runner. See stack logs under Platform/.logs/webrtc-stack/<timestamp>/.",
        cancellationToken);
    await File.WriteAllTextAsync(
        stderrPath,
        "Bootstrap stderr is not redirected by the runner. See stack logs under Platform/.logs/webrtc-stack/<timestamp>/.",
        cancellationToken);

    if (!stackReady && process.ExitCode != 0)
    {
        if (timedOutWithoutSummary)
        {
            throw new InvalidOperationException(
                $"WebRTC stack bootstrap timed out after {bootstrapTimeout.TotalSeconds:0}s without summary output: {stackSummaryPath}");
        }

        throw new InvalidOperationException(
            $"WebRTC stack bootstrap failed with exit code {process.ExitCode}. Summary JSON was not produced: {stackSummaryPath}");
    }

    var stackJson = TryReadJson(stackSummaryPath);
    if (stackJson is not { } stack)
    {
        throw new InvalidOperationException($"WebRTC stack bootstrap did not produce summary JSON: {stackSummaryPath}");
    }

    var adapterPid = stack.TryGetProperty("adapterPid", out var adapterPidElement)
                     && adapterPidElement.ValueKind == JsonValueKind.Number
                     && adapterPidElement.TryGetInt32(out var parsedAdapterPid)
        ? parsedAdapterPid
        : (int?)null;
    var exp022Pid = stack.TryGetProperty("exp022Pid", out var exp022PidElement)
                    && exp022PidElement.ValueKind == JsonValueKind.Number
                    && exp022PidElement.TryGetInt32(out var parsedExp022Pid)
        ? parsedExp022Pid
        : (int?)null;

    Console.WriteLine($"PASS  WebRTC Stack (adapterPid={adapterPid?.ToString() ?? "n/a"})");
    if (terminatedAfterSummary)
    {
        Console.WriteLine("Note: bootstrap script process was terminated after summary was produced.");
    }
    Console.WriteLine($"Log:   {stdoutPath}");

    return new StackBootstrapRuntime(adapterPid, exp022Pid);
}

static void StopProcessById(int? pid, string label)
{
    if (pid is null or <= 0)
    {
        return;
    }

    try
    {
        using var process = Process.GetProcessById(pid.Value);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            Console.WriteLine($"Stopped {label} PID {pid.Value}");
        }
    }
    catch
    {
        // best-effort cleanup
    }
}

static async Task<string> AcquireAccessTokenAsync(AcceptanceRunnerOptions options, HttpClient client, CancellationToken cancellationToken)
{
    var baseUrlCandidates = BuildKeycloakBaseUrlCandidates(options.KeycloakBaseUrl);
    var scopeCandidates = BuildScopeCandidates(options.TokenScope);
    var attempts = Math.Max(1, options.TokenRequestAttempts);
    var delayMs = Math.Max(100, options.TokenRequestDelayMs);
    Console.WriteLine(
        $"Acquiring access token (client_id={options.TokenClientId}, username={options.TokenUsername}, authorities={baseUrlCandidates.Count}, scopes={scopeCandidates.Count}).");

    Exception? last = null;
    foreach (var baseUrl in baseUrlCandidates)
    {
        var tokenEndpoint = $"{baseUrl.TrimEnd('/')}/realms/{options.RealmName}/protocol/openid-connect/token";
        foreach (var scope in scopeCandidates)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    using var body = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "password"),
                        new KeyValuePair<string, string>("client_id", options.TokenClientId),
                        new KeyValuePair<string, string>("client_secret", options.TokenClientSecret),
                        new KeyValuePair<string, string>("username", options.TokenUsername),
                        new KeyValuePair<string, string>("password", options.TokenPassword),
                        new KeyValuePair<string, string>("scope", scope),
                    }.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)));

                    using var response = await client.PostAsync(tokenEndpoint, body, cancellationToken);
                    var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"Token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}) at {tokenEndpoint} (scope='{scope}'). Body: {payload}");
                    }

                    using var doc = JsonDocument.Parse(payload);
                    var token = doc.RootElement.GetProperty("access_token").GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }

                    throw new InvalidOperationException(
                        $"OIDC token response did not contain access_token at {tokenEndpoint} (scope='{scope}').");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Console.WriteLine(
                        $"Token attempt {attempt}/{attempts} failed at {tokenEndpoint} (scope='{scope}'): {ex.GetBaseException().Message}");
                    if (attempt < attempts)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }
        }
    }

    throw new InvalidOperationException(
        $"Keycloak token request failed after {attempts} attempt(s) across {baseUrlCandidates.Count} authority candidate(s).",
        last);
}

static IReadOnlyList<string> BuildKeycloakBaseUrlCandidates(string keycloakBaseUrl)
{
    var candidates = new List<string>();
    void Add(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.TrimEnd('/');
        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }
    }

    Add(keycloakBaseUrl);
    if (!Uri.TryCreate(keycloakBaseUrl, UriKind.Absolute, out var uri))
    {
        return candidates;
    }

    var portSuffix = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
    if (string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
    {
        Add($"{uri.Scheme}://127.0.0.1{portSuffix}");
        Add($"{uri.Scheme}://localhost{portSuffix}");
    }
    else if (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
             || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        Add($"{uri.Scheme}://host.docker.internal{portSuffix}");
    }

    return candidates;
}

static IReadOnlyList<string> BuildScopeCandidates(string tokenScope)
{
    var scopes = new List<string>();
    void Add(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (!scopes.Contains(value, StringComparer.Ordinal))
        {
            scopes.Add(value);
        }
    }

    Add(tokenScope);
    Add("openid");
    Add(string.Empty);
    return scopes;
}

static async Task MarkPeerOnlineAsync(
    AcceptanceRunnerOptions options,
    HttpClient client,
    SessionRuntime runtime,
    CancellationToken cancellationToken)
{
    var uri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(runtime.SessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(runtime.PeerId)}/online";
    var body = new
    {
        endpointId = runtime.EndpointId,
        metadata = new Dictionary<string, string>
        {
            ["adapter"] = "webrtc-edge-agent-smoke-dotnet",
            ["state"] = "Connected",
            ["principalId"] = options.PrincipalId,
        },
    };
    using var response = await client.PostAsJsonAsync(uri, body, cancellationToken);
    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return;
    }

    response.EnsureSuccessStatusCode();
}

static async Task<SessionRuntime> EnsureControlLeaseAsync(
    AcceptanceRunnerOptions options,
    HttpClient client,
    SessionRuntime runtime,
    CancellationToken cancellationToken)
{
    if (options.SkipControlLeaseRequest)
    {
        return runtime;
    }

    var uri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(runtime.SessionId)}/control/ensure";
    var body = new
    {
        participantId = $"owner:{options.PrincipalId}",
        requestedBy = options.PrincipalId,
        endpointId = runtime.EndpointId,
        profile = "UltraLowLatency",
        leaseSeconds = Math.Max(30, options.LeaseSeconds),
        reason = "webrtc edge-agent smoke (dotnet)",
        autoCreateSessionIfMissing = true,
        // Keep smoke command-path bound to the stack-bootstrap session/peer and avoid
        // session ID rebinds that can disconnect acceptance from the live edge adapter.
        preferLiveRelaySession = false,
        tenantId = "local-tenant",
        organizationId = "local-org",
    };

    using var response = await client.PostAsJsonAsync(uri, body, cancellationToken);
    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return runtime;
    }

    response.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    if (!doc.RootElement.TryGetProperty("effectiveSessionId", out var effective))
    {
        return runtime;
    }

    var effectiveSessionId = effective.GetString();
    return string.IsNullOrWhiteSpace(effectiveSessionId)
        ? runtime
        : runtime with { SessionId = effectiveSessionId };
}

static async Task<object?> WaitRelayTransportReadyAsync(
    AcceptanceRunnerOptions options,
    HttpClient client,
    string sessionId,
    CancellationToken cancellationToken)
{
    var attempts = Math.Max(1, options.TransportHealthAttempts);
    if (options.PeerReadyTimeoutSec > 0)
    {
        var byTimeout = (int)Math.Ceiling((Math.Max(5, options.PeerReadyTimeoutSec) * 1000.0) / Math.Max(100, options.TransportHealthDelayMs));
        attempts = Math.Max(attempts, byTimeout);
    }

    var delay = Math.Max(100, options.TransportHealthDelayMs);
    var readinessUri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/readiness?provider=webrtc-datachannel";
    object? last = null;
    for (var i = 0; i < attempts; i++)
    {
        try
        {
            using var response = await client.GetAsync(readinessUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.ValueKind == JsonValueKind.True;
            last = JsonSerializer.Deserialize<object>(root.GetRawText());
            if (ready)
            {
                return last;
            }
        }
        catch
        {
            // keep polling
        }

        await Task.Delay(delay, cancellationToken);
    }

    return last;
}

static async Task<MediaGateResult> WaitMediaGateAsync(
    AcceptanceRunnerOptions options,
    HttpClient client,
    SessionRuntime runtime,
    CancellationToken cancellationToken)
{
    var attempts = Math.Max(1, options.MediaHealthAttempts);
    var delayMs = Math.Max(100, options.MediaHealthDelayMs);
    var readinessUri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(runtime.SessionId)}/transport/readiness?provider=webrtc-datachannel";
    var streamsUri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(runtime.SessionId)}/transport/media/streams?peerId={Uri.EscapeDataString(runtime.PeerId)}&endpointId={Uri.EscapeDataString(runtime.EndpointId)}";

    object? lastReadiness = null;
    object? lastStreams = null;
    bool observedMediaReady = false;
    string? observedPlaybackUrl = null;

    for (var i = 0; i < attempts; i++)
    {
        try
        {
            using var readinessResponse = await client.GetAsync(readinessUri, cancellationToken);
            if (readinessResponse.IsSuccessStatusCode)
            {
                using var readinessDoc = JsonDocument.Parse(await readinessResponse.Content.ReadAsStringAsync(cancellationToken));
                var readinessRoot = readinessDoc.RootElement.Clone();
                lastReadiness = JsonSerializer.Deserialize<object>(readinessRoot.GetRawText());

                if (TryReadBoolean(readinessRoot, "mediaReady") is true)
                {
                    observedMediaReady = true;
                }

                observedPlaybackUrl ??= TryReadString(readinessRoot, "mediaPlaybackUrl");
            }
        }
        catch
        {
            // keep polling
        }

        try
        {
            using var streamsResponse = await client.GetAsync(streamsUri, cancellationToken);
            if (streamsResponse.IsSuccessStatusCode)
            {
                using var streamsDoc = JsonDocument.Parse(await streamsResponse.Content.ReadAsStringAsync(cancellationToken));
                var streamsRoot = streamsDoc.RootElement.Clone();
                lastStreams = JsonSerializer.Deserialize<object>(streamsRoot.GetRawText());
                if (streamsRoot.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in streamsRoot.EnumerateArray())
                    {
                        if (TryReadBoolean(item, "ready") is true)
                        {
                            observedMediaReady = true;
                        }

                        observedPlaybackUrl ??= TryReadString(item, "playbackUrl");
                    }
                }
            }
        }
        catch
        {
            // keep polling
        }

        var mediaReadySatisfied = !options.RequireMediaReady || observedMediaReady;
        var playbackSatisfied = !options.RequireMediaPlaybackUrl || !string.IsNullOrWhiteSpace(observedPlaybackUrl);
        if (mediaReadySatisfied && playbackSatisfied)
        {
            return new MediaGateResult(
                Pass: true,
                ObservedMediaReady: observedMediaReady,
                ObservedPlaybackUrl: observedPlaybackUrl,
                ReadinessSnapshot: lastReadiness,
                StreamsSnapshot: lastStreams,
                FailureReason: null);
        }

        await Task.Delay(delayMs, cancellationToken);
    }

    var reasons = new List<string>();
    if (options.RequireMediaReady && !observedMediaReady)
    {
        reasons.Add("mediaReady flag did not become true");
    }

    if (options.RequireMediaPlaybackUrl && string.IsNullOrWhiteSpace(observedPlaybackUrl))
    {
        reasons.Add("mediaPlaybackUrl/playbackUrl is missing");
    }

    return new MediaGateResult(
        Pass: false,
        ObservedMediaReady: observedMediaReady,
        ObservedPlaybackUrl: observedPlaybackUrl,
        ReadinessSnapshot: lastReadiness,
        StreamsSnapshot: lastStreams,
        FailureReason: reasons.Count == 0
            ? "Media gate did not satisfy required conditions."
            : string.Join("; ", reasons));
}

static bool? TryReadBoolean(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null,
    };
}

static string? TryReadString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        _ => null,
    };
}

static async Task<CommandAck> DispatchCommandAsync(
    AcceptanceRunnerOptions options,
    HttpClient client,
    SessionRuntime runtime,
    string commandId,
    string commandText,
    CancellationToken cancellationToken)
{
    var uri = $"{options.ApiBaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(runtime.SessionId)}/commands";
    var payload = new
    {
        commandId,
        sessionId = runtime.SessionId,
        channel = "Hid",
        action = options.CommandAction,
        args = new
        {
            text = commandText,
            transportProvider = "webrtc-datachannel",
            recipientPeerId = runtime.PeerId,
            participantId = $"owner:{options.PrincipalId}",
            principalId = options.PrincipalId,
        },
        timeoutMs = Math.Max(1000, options.TimeoutMs),
        idempotencyKey = $"idem-{commandId}",
        tenantId = "local-tenant",
        organizationId = "local-org",
    };

    using var response = await client.PostAsJsonAsync(uri, payload, cancellationToken);
    response.EnsureSuccessStatusCode();
    var raw = await response.Content.ReadAsStringAsync(cancellationToken);
    using var doc = JsonDocument.Parse(raw);
    var status = doc.RootElement.TryGetProperty("status", out var statusElement)
        ? statusElement.GetString() ?? "Unknown"
        : "Unknown";
    return new CommandAck(commandId, status, JsonSerializer.Deserialize<object>(raw));
}

static bool IsRetryable(CommandAck ack)
{
    if (string.Equals(ack.Status, "Timeout", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!string.Equals(ack.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (ack.Raw is not JsonElement element || !element.TryGetProperty("error", out var error))
    {
        return false;
    }

    if (!error.TryGetProperty("code", out var codeElement))
    {
        return false;
    }

    var code = codeElement.GetString();
    return !string.IsNullOrWhiteSpace(code) &&
           code.StartsWith("E_TRANSPORT_", StringComparison.OrdinalIgnoreCase);
}

static string ResolvePlatformRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return Path.GetFullPath(overridePath);
    }

    var current = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(current, "run.ps1")))
    {
        return current;
    }

    var parent = Directory.GetParent(current)?.FullName;
    if (parent is not null && File.Exists(Path.Combine(parent, "run.ps1")))
    {
        return parent;
    }

    return current;
}

static JsonElement? TryReadJson(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        var content = File.ReadAllText(path);
        if (!string.IsNullOrEmpty(content) && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

static async Task WriteSummaryAsync(string outputPath, AcceptanceSummary summary, string platformRoot)
{
    var resolved = ResolveOutputPath(outputPath, platformRoot);
    var dir = Path.GetDirectoryName(resolved);
    if (!string.IsNullOrWhiteSpace(dir))
    {
        Directory.CreateDirectory(dir);
    }

    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
    {
        WriteIndented = true,
    });
    await File.WriteAllTextAsync(resolved, json);
}

static async Task WriteJsonAsync(string path, object payload)
{
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(dir))
    {
        Directory.CreateDirectory(dir);
    }

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(path, json);
}

static string ResolveOutputPath(string outputPath, string platformRoot)
{
    if (Path.IsPathRooted(outputPath))
    {
        return outputPath;
    }

    return Path.GetFullPath(Path.Combine(platformRoot, outputPath));
}

internal sealed class AcceptanceRunnerOptions
{
    public string ApiBaseUrl { get; private set; } = "http://127.0.0.1:18093";
    public string CommandExecutor { get; private set; } = "uart";
    public bool AllowLegacyControlWs { get; private set; }
    public string ControlHealthUrl { get; private set; } = "http://127.0.0.1:28092/health";
    public string ControlWsUrl { get; private set; } = string.Empty;
    public string KeycloakBaseUrl { get; private set; } = "http://host.docker.internal:18096";
    public string RealmName { get; private set; } = "hidbridge-dev";
    public string TokenClientId { get; private set; } = "controlplane-smoke";
    public string TokenClientSecret { get; private set; } = string.Empty;
    public string TokenScope { get; private set; } = "openid";
    public string TokenUsername { get; private set; } = "operator.smoke.admin";
    public string TokenPassword { get; private set; } = "ChangeMe123!";
    public int RequestTimeoutSec { get; private set; } = 15;
    public int ControlHealthAttempts { get; private set; } = 20;
    public int KeycloakHealthAttempts { get; private set; } = 60;
    public int KeycloakHealthDelayMs { get; private set; } = 500;
    public int TokenRequestAttempts { get; private set; } = 5;
    public int TokenRequestDelayMs { get; private set; } = 500;
    public bool SkipTransportHealthCheck { get; private set; }
    public int TransportHealthAttempts { get; private set; } = 20;
    public int TransportHealthDelayMs { get; private set; } = 500;
    public string UartPort { get; private set; } = "COM6";
    public int UartBaud { get; private set; } = 3_000_000;
    public string UartHmacKey { get; private set; } = "your-master-secret";
    public string PrincipalId { get; private set; } = "smoke-runner";
    public int PeerReadyTimeoutSec { get; private set; } = 45;
    public string SessionEnvPath { get; private set; } = "Platform/.logs/webrtc-peer-adapter.session.env";
    public string SessionId { get; private set; } = string.Empty;
    public string PeerId { get; private set; } = string.Empty;
    public string EndpointId { get; private set; } = string.Empty;
    public string CommandAction { get; private set; } = "keyboard.text";
    public int TimeoutMs { get; private set; } = 8000;
    public int CommandAttempts { get; private set; } = 3;
    public int CommandRetryDelayMs { get; private set; } = 750;
    public bool RequireMediaReady { get; private set; }
    public bool RequireMediaPlaybackUrl { get; private set; }
    public int MediaHealthAttempts { get; private set; } = 20;
    public int MediaHealthDelayMs { get; private set; } = 500;
    public bool SkipControlLeaseRequest { get; private set; }
    public int LeaseSeconds { get; private set; } = 120;
    public bool SkipRuntimeBootstrap { get; private set; }
    public bool StopExisting { get; private set; }
    public bool StopStackAfter { get; private set; }
    public string OutputJsonPath { get; private set; } = "Platform/.logs/webrtc-edge-agent-acceptance.result.json";
    public string PlatformRoot { get; private set; } = string.Empty;

    public static AcceptanceRunnerOptions Parse(string[] args)
    {
        var options = new AcceptanceRunnerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..].ToLowerInvariant();
            switch (key)
            {
                case "allow-legacy-controlws": options.AllowLegacyControlWs = true; break;
                case "skip-transport-health-check": options.SkipTransportHealthCheck = true; break;
                case "skip-runtime-bootstrap": options.SkipRuntimeBootstrap = true; break;
                case "stop-existing": options.StopExisting = true; break;
                case "stop-stack-after": options.StopStackAfter = true; break;
                case "skip-control-lease-request": options.SkipControlLeaseRequest = true; break;
                case "api-base-url": options.ApiBaseUrl = RequireValue(args, ref i, arg); break;
                case "command-executor": options.CommandExecutor = RequireValue(args, ref i, arg); break;
                case "command-action": options.CommandAction = RequireValue(args, ref i, arg); break;
                case "timeout-ms": options.TimeoutMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "command-attempts": options.CommandAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "command-retry-delay-ms": options.CommandRetryDelayMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "require-media-ready": options.RequireMediaReady = true; break;
                case "require-media-playback-url": options.RequireMediaPlaybackUrl = true; break;
                case "media-health-attempts": options.MediaHealthAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "media-health-delay-ms": options.MediaHealthDelayMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "lease-seconds": options.LeaseSeconds = int.Parse(RequireValue(args, ref i, arg)); break;
                case "control-health-url": options.ControlHealthUrl = RequireValue(args, ref i, arg); break;
                case "control-ws-url": options.ControlWsUrl = RequireValue(args, ref i, arg); break;
                case "keycloak-base-url": options.KeycloakBaseUrl = RequireValue(args, ref i, arg); break;
                case "realm-name": options.RealmName = RequireValue(args, ref i, arg); break;
                case "token-client-id": options.TokenClientId = RequireValue(args, ref i, arg); break;
                case "token-client-secret": options.TokenClientSecret = RequireValue(args, ref i, arg); break;
                case "token-scope": options.TokenScope = RequireValue(args, ref i, arg); break;
                case "token-username": options.TokenUsername = RequireValue(args, ref i, arg); break;
                case "token-password": options.TokenPassword = RequireValue(args, ref i, arg); break;
                case "request-timeout-sec": options.RequestTimeoutSec = int.Parse(RequireValue(args, ref i, arg)); break;
                case "control-health-attempts": options.ControlHealthAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "keycloak-health-attempts": options.KeycloakHealthAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "keycloak-health-delay-ms": options.KeycloakHealthDelayMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "token-request-attempts": options.TokenRequestAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "token-request-delay-ms": options.TokenRequestDelayMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "transport-health-attempts": options.TransportHealthAttempts = int.Parse(RequireValue(args, ref i, arg)); break;
                case "transport-health-delay-ms": options.TransportHealthDelayMs = int.Parse(RequireValue(args, ref i, arg)); break;
                case "uart-port": options.UartPort = RequireValue(args, ref i, arg); break;
                case "uart-baud": options.UartBaud = int.Parse(RequireValue(args, ref i, arg)); break;
                case "uart-hmac-key": options.UartHmacKey = RequireValue(args, ref i, arg); break;
                case "principal-id": options.PrincipalId = RequireValue(args, ref i, arg); break;
                case "peer-ready-timeout-sec": options.PeerReadyTimeoutSec = int.Parse(RequireValue(args, ref i, arg)); break;
                case "session-env-path": options.SessionEnvPath = RequireValue(args, ref i, arg); break;
                case "session-id": options.SessionId = RequireValue(args, ref i, arg); break;
                case "peer-id": options.PeerId = RequireValue(args, ref i, arg); break;
                case "endpoint-id": options.EndpointId = RequireValue(args, ref i, arg); break;
                case "output-json-path": options.OutputJsonPath = RequireValue(args, ref i, arg); break;
                case "platform-root": options.PlatformRoot = RequireValue(args, ref i, arg); break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {argument}");
        }

        index++;
        return args[index];
    }
}

internal sealed class AcceptanceSummary
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string CommandExecutor { get; set; } = string.Empty;
    public string StackSummaryPath { get; set; } = string.Empty;
    public string SmokeSummaryPath { get; set; } = string.Empty;
    public JsonElement? Stack { get; set; }
    public JsonElement? Smoke { get; set; }
    public bool Pass { get; set; }
}

internal sealed record StackBootstrapRuntime(int? AdapterPid, int? Exp022Pid);

internal sealed record SessionRuntime(string SessionId, string PeerId, string EndpointId);

internal sealed record CommandAck(string CommandId, string Status, object? Raw);

internal sealed record SmokeCommandAttempt(
    int Attempt,
    string CommandId,
    string Status,
    DateTimeOffset ReportedAtUtc);

internal sealed class SmokeSummary
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string CommandAction { get; set; } = string.Empty;
    public string CommandStatus { get; set; } = string.Empty;
    public object? CommandResponse { get; set; }
    public int CommandAttemptsConfigured { get; set; }
    public IReadOnlyList<SmokeCommandAttempt> CommandAttempts { get; set; } = [];
    public object? TransportReadiness { get; set; }
    public bool MediaGateRequired { get; set; }
    public bool? MediaGatePass { get; set; }
    public bool? MediaReadyObserved { get; set; }
    public string? MediaPlaybackUrlObserved { get; set; }
    public string? MediaGateFailureReason { get; set; }
    public object? MediaReadiness { get; set; }
    public object? MediaStreams { get; set; }
    public string? OutputPath { get; set; }
}

internal sealed record MediaGateResult(
    bool Pass,
    bool ObservedMediaReady,
    string? ObservedPlaybackUrl,
    object? ReadinessSnapshot,
    object? StreamsSnapshot,
    string? FailureReason);
