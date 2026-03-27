using System.ComponentModel.DataAnnotations;

namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Configuration for the WebRTC edge proxy worker.
/// </summary>
public sealed class EdgeProxyOptions
{
    [Required]
    public string BaseUrl { get; set; } = "http://127.0.0.1:18093";

    [Required]
    public string SessionId { get; set; } = "";

    [Required]
    public string PeerId { get; set; } = "";

    [Required]
    public string EndpointId { get; set; } = "";

    /// <summary>
    /// Selects command execution path: <c>uart</c> (default) or legacy <c>controlws</c>.
    /// </summary>
    public string CommandExecutor { get; set; } = "uart";

    /// <summary>
    /// Selects edge control transport engine: <c>dcd</c> (default, production path) or <c>relay</c> (compatibility fallback).
    /// </summary>
    public string TransportEngine { get; set; } = "dcd";

    /// <summary>
    /// Allows <c>TransportEngine=dcd</c> to fall back to relay command queue when no direct signal commands are available.
    /// </summary>
    public bool DcdAllowRelayFallback { get; set; } = true;

    /// <summary>
    /// Selects media runtime engine: <c>ffmpeg-dcd</c> (default) or <c>none</c> (compatibility fallback).
    /// </summary>
    public string MediaEngine { get; set; } = "ffmpeg-dcd";

    /// <summary>
    /// Engine switch strategy:
    /// <c>fixed</c>, <c>shadow</c>, <c>canary</c>, <c>default-switch</c>.
    /// </summary>
    public string EngineSwitchMode { get; set; } = "default-switch";

    /// <summary>
    /// Enables shadow mode execution semantics (DCD media/control with forced relay compatibility fallback).
    /// </summary>
    public bool EngineShadowMode { get; set; }

    /// <summary>
    /// Canary percentage (0..100) used when <see cref="EngineSwitchMode"/> is <c>canary</c>.
    /// </summary>
    public int EngineCanaryPercent { get; set; } = 100;

    /// <summary>
    /// Enforces SLO thresholds and forces relay/none fallback when exceeded.
    /// </summary>
    public bool EngineSloEnforce { get; set; } = true;

    /// <summary>
    /// Minimum command sample size before SLO enforcement is evaluated.
    /// </summary>
    public int EngineSloMinCommandSampleCount { get; set; } = 20;

    /// <summary>
    /// Maximum allowed timeout/reject rate (percent) before fallback.
    /// </summary>
    public double EngineSloAckTimeoutRateMaxPct { get; set; } = 5.0;

    /// <summary>
    /// Maximum allowed reconnect transitions per hour before fallback.
    /// </summary>
    public double EngineSloReconnectFrequencyMaxPerHour { get; set; } = 8.0;

    /// <summary>
    /// Maximum allowed p95 command roundtrip (ms) before fallback.
    /// </summary>
    public int EngineSloRoundtripP95MsMax { get; set; } = 2000;

    /// <summary>
    /// Optional ffmpeg executable path used by <c>MediaEngine=ffmpeg-dcd</c>.
    /// </summary>
    public string FfmpegExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional ffmpeg arguments template used by <c>MediaEngine=ffmpeg-dcd</c>.
    /// Supports placeholders: <c>{sessionId}</c>, <c>{peerId}</c>, <c>{endpointId}</c>, <c>{streamId}</c>, <c>{source}</c>, <c>{baseUrl}</c>, <c>{whipUrl}</c>, <c>{whepUrl}</c>, <c>{whipBearerToken}</c>.
    /// </summary>
    public string FfmpegArgumentsTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Graceful ffmpeg stop timeout in milliseconds before forced kill.
    /// </summary>
    public int FfmpegStopTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Marks runtime as degraded when ffmpeg progress telemetry is stale for longer than this threshold.
    /// </summary>
    public int FfmpegTelemetryStaleAfterSec { get; set; } = 10;

    [Required]
    public string ControlWsUrl { get; set; } = "ws://127.0.0.1:28092/ws/control";

    /// <summary>
    /// UART serial port used when <see cref="CommandExecutor"/> is configured as <c>uart</c>.
    /// </summary>
    public string UartPort { get; set; } = "";

    /// <summary>
    /// UART baudrate used when <see cref="CommandExecutor"/> is configured as <c>uart</c>.
    /// </summary>
    public int UartBaud { get; set; } = 3000000;

    /// <summary>
    /// UART firmware HMAC key.
    /// </summary>
    public string UartHmacKey { get; set; } = "changeme";

    /// <summary>
    /// Optional master secret used to derive per-device UART HMAC key material.
    /// </summary>
    public string UartMasterSecret { get; set; } = "";

    /// <summary>
    /// Mouse interface selector (0xFF means auto-resolve mouse interface from firmware inventory).
    /// </summary>
    public int UartMouseInterfaceSelector { get; set; } = 0xFF;

    /// <summary>
    /// Keyboard interface selector (0xFE means auto-resolve keyboard interface from firmware inventory).
    /// </summary>
    public int UartKeyboardInterfaceSelector { get; set; } = 0xFE;

    /// <summary>
    /// UART command timeout in milliseconds.
    /// </summary>
    public int UartCommandTimeoutMs { get; set; } = 300;

    /// <summary>
    /// UART inject timeout in milliseconds.
    /// </summary>
    public int UartInjectTimeoutMs { get; set; } = 200;

    /// <summary>
    /// UART inject retry count.
    /// </summary>
    public int UartInjectRetries { get; set; } = 2;

    /// <summary>
    /// Releases serial port handle after each executed command.
    /// </summary>
    public bool UartReleasePortAfterExecute { get; set; }

    /// <summary>
    /// Optional HTTP endpoint that exposes capture/media health (for example <c>http://127.0.0.1:28092/health</c>).
    /// </summary>
    public string MediaHealthUrl { get; set; } = string.Empty;

    /// <summary>
    /// Timeout for media-health probe requests in seconds.
    /// </summary>
    public int MediaHealthTimeoutSec { get; set; } = 3;

    /// <summary>
    /// Logical stream identifier used by readiness projections.
    /// </summary>
    public string MediaStreamId { get; set; } = "edge-main";

    /// <summary>
    /// Human-readable media source descriptor (for example <c>hdmi-usb-capture</c>).
    /// </summary>
    public string MediaSource { get; set; } = "edge-capture";

    /// <summary>
    /// Optional playback URL surfaced in transport diagnostics (for example <c>http://127.0.0.1:8080/live.m3u8</c>).
    /// </summary>
    public string MediaPlaybackUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional WHIP publish endpoint used by preview <c>MediaEngine=ffmpeg-dcd</c>.
    /// </summary>
    public string MediaWhipUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional WHEP playback endpoint used by preview <c>MediaEngine=ffmpeg-dcd</c>.
    /// </summary>
    public string MediaWhepUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional bearer token injected into ffmpeg WHIP publish requests by argument templates.
    /// </summary>
    public string MediaWhipBearerToken { get; set; } = string.Empty;

    /// <summary>
    /// Enables agent-managed startup of local media backend process (for example SRS binary/service wrapper).
    /// </summary>
    public bool MediaBackendAutoStart { get; set; }

    /// <summary>
    /// Executable path used when <see cref="MediaBackendAutoStart"/> is enabled.
    /// </summary>
    public string MediaBackendExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional arguments template for media backend process.
    /// Supports placeholders: <c>{sessionId}</c>, <c>{peerId}</c>, <c>{endpointId}</c>, <c>{streamId}</c>, <c>{source}</c>, <c>{baseUrl}</c>, <c>{whipUrl}</c>, <c>{whepUrl}</c>.
    /// </summary>
    public string MediaBackendArgumentsTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Optional working directory for media backend process.
    /// </summary>
    public string MediaBackendWorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Startup timeout for media backend auto-start probe.
    /// </summary>
    public int MediaBackendStartupTimeoutSec { get; set; } = 20;

    /// <summary>
    /// Probe delay between startup reachability checks.
    /// </summary>
    public int MediaBackendProbeDelayMs { get; set; } = 500;

    /// <summary>
    /// TCP probe timeout when validating backend endpoint reachability.
    /// </summary>
    public int MediaBackendProbeTimeoutMs { get; set; } = 1200;

    /// <summary>
    /// Stop timeout for media backend process before forced kill.
    /// </summary>
    public int MediaBackendStopTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Requires media probe to report ready before readiness policy can pass.
    /// </summary>
    public bool RequireMediaReady { get; set; } = true;

    /// <summary>
    /// Treats missing probe URL as ready (useful for control-only scenarios without capture path).
    /// </summary>
    public bool AssumeMediaReadyWithoutProbe { get; set; }

    public string PrincipalId { get; set; } = "smoke-runner";
    public string TenantId { get; set; } = "local-tenant";
    public string OrganizationId { get; set; } = "local-org";

    /// <summary>
    /// Caller roles projected into <c>X-HidBridge-Role</c> header (comma/semicolon separated).
    /// </summary>
    public string OperatorRolesCsv { get; set; } = "operator.edge";

    public string AccessToken { get; set; } = "";
    public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
    public string KeycloakRealm { get; set; } = "hidbridge-dev";
    public string TokenClientId { get; set; } = "controlplane-smoke";
    public string TokenClientSecret { get; set; } = string.Empty;
    public string TokenUsername { get; set; } = "operator.smoke.admin";
    public string TokenPassword { get; set; } = "ChangeMe123!";
    public string TokenRefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Allows fallback to password grant when refresh-token grant is unavailable or fails.
    /// </summary>
    public bool AllowPasswordGrantFallback { get; set; } = true;

    /// <summary>
    /// Optional OIDC scope used by password and refresh grants.
    /// </summary>
    public string TokenScope { get; set; } = string.Empty;

    /// <summary>
    /// Proactive refresh window before access-token expiry.
    /// </summary>
    public int TokenRefreshSkewSec { get; set; } = 60;

    public int PollIntervalMs { get; set; } = 250;
    public int BatchLimit { get; set; } = 50;
    public int HeartbeatIntervalSec { get; set; } = 10;
    public int CommandTimeoutMs { get; set; } = 10000;
    public int HttpTimeoutSec { get; set; } = 30;
    public int ReconnectBackoffMinMs { get; set; } = 500;
    public int ReconnectBackoffMaxMs { get; set; } = 5000;
    public int ReconnectBackoffJitterMs { get; set; } = 250;
    public int TransientFailureThresholdForOffline { get; set; } = 2;

    /// <summary>
    /// Normalizes numeric and URL values to safe runtime bounds.
    /// </summary>
    public void Normalize()
    {
        BaseUrl = BaseUrl.TrimEnd('/');
        CommandExecutor = (CommandExecutor ?? string.Empty).Trim();
        TransportEngine = (TransportEngine ?? string.Empty).Trim();
        MediaEngine = (MediaEngine ?? string.Empty).Trim();
        EngineSwitchMode = (EngineSwitchMode ?? string.Empty).Trim();
        FfmpegExecutablePath = (FfmpegExecutablePath ?? string.Empty).Trim();
        FfmpegArgumentsTemplate = (FfmpegArgumentsTemplate ?? string.Empty).Trim();
        FfmpegStopTimeoutMs = Math.Max(250, FfmpegStopTimeoutMs);
        FfmpegTelemetryStaleAfterSec = Math.Max(2, FfmpegTelemetryStaleAfterSec);
        EngineCanaryPercent = Math.Clamp(EngineCanaryPercent, 0, 100);
        EngineSloMinCommandSampleCount = Math.Max(1, EngineSloMinCommandSampleCount);
        EngineSloAckTimeoutRateMaxPct = Math.Clamp(EngineSloAckTimeoutRateMaxPct, 0.0, 100.0);
        EngineSloReconnectFrequencyMaxPerHour = Math.Max(0.0, EngineSloReconnectFrequencyMaxPerHour);
        EngineSloRoundtripP95MsMax = Math.Max(100, EngineSloRoundtripP95MsMax);
        PollIntervalMs = Math.Max(100, PollIntervalMs);
        BatchLimit = Math.Clamp(BatchLimit, 1, 500);
        HeartbeatIntervalSec = Math.Max(3, HeartbeatIntervalSec);
        CommandTimeoutMs = Math.Max(1000, CommandTimeoutMs);
        HttpTimeoutSec = Math.Max(5, HttpTimeoutSec);
        MediaHealthTimeoutSec = Math.Max(1, MediaHealthTimeoutSec);
        UartBaud = Math.Max(1200, UartBaud);
        UartCommandTimeoutMs = Math.Max(50, UartCommandTimeoutMs);
        UartInjectTimeoutMs = Math.Max(50, UartInjectTimeoutMs);
        UartInjectRetries = Math.Clamp(UartInjectRetries, 0, 10);
        UartMouseInterfaceSelector = Math.Clamp(UartMouseInterfaceSelector, byte.MinValue, byte.MaxValue);
        UartKeyboardInterfaceSelector = Math.Clamp(UartKeyboardInterfaceSelector, byte.MinValue, byte.MaxValue);
        ReconnectBackoffMinMs = Math.Max(100, ReconnectBackoffMinMs);
        ReconnectBackoffMaxMs = Math.Max(ReconnectBackoffMinMs, ReconnectBackoffMaxMs);
        ReconnectBackoffJitterMs = Math.Max(0, ReconnectBackoffJitterMs);
        TransientFailureThresholdForOffline = Math.Max(1, TransientFailureThresholdForOffline);
        TokenRefreshSkewSec = Math.Max(5, TokenRefreshSkewSec);
        TokenScope = TokenScope?.Trim() ?? string.Empty;
        TokenClientSecret = TokenClientSecret?.Trim() ?? string.Empty;
        TokenRefreshToken = TokenRefreshToken?.Trim() ?? string.Empty;
        MediaPlaybackUrl = MediaPlaybackUrl?.Trim() ?? string.Empty;
        MediaWhipUrl = MediaWhipUrl?.Trim() ?? string.Empty;
        MediaWhepUrl = MediaWhepUrl?.Trim() ?? string.Empty;
        MediaWhipBearerToken = MediaWhipBearerToken?.Trim() ?? string.Empty;
        MediaBackendExecutablePath = MediaBackendExecutablePath?.Trim() ?? string.Empty;
        MediaBackendArgumentsTemplate = MediaBackendArgumentsTemplate?.Trim() ?? string.Empty;
        MediaBackendWorkingDirectory = MediaBackendWorkingDirectory?.Trim() ?? string.Empty;
        MediaBackendStartupTimeoutSec = Math.Max(3, MediaBackendStartupTimeoutSec);
        MediaBackendProbeDelayMs = Math.Max(100, MediaBackendProbeDelayMs);
        MediaBackendProbeTimeoutMs = Math.Max(200, MediaBackendProbeTimeoutMs);
        MediaBackendStopTimeoutMs = Math.Max(250, MediaBackendStopTimeoutMs);
        OperatorRolesCsv = string.Join(
            ",",
            (OperatorRolesCsv ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static role => role.Trim())
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(OperatorRolesCsv))
        {
            OperatorRolesCsv = "operator.edge";
        }
    }

    /// <summary>
    /// Resolves configured command executor kind.
    /// </summary>
    public EdgeProxyCommandExecutorKind GetCommandExecutorKind()
    {
        if (TryParseCommandExecutorKind(CommandExecutor, out var kind))
        {
            return kind;
        }

        return EdgeProxyCommandExecutorKind.UartHid;
    }

    /// <summary>
    /// Resolves configured control transport engine kind.
    /// </summary>
    public EdgeProxyTransportEngineKind GetTransportEngineKind()
    {
        if (TryParseTransportEngineKind(TransportEngine, out var kind))
        {
            return kind;
        }

        return EdgeProxyTransportEngineKind.RelayCompat;
    }

    /// <summary>
    /// Resolves configured media runtime engine kind.
    /// </summary>
    public EdgeProxyMediaEngineKind GetMediaEngineKind()
    {
        if (TryParseMediaEngineKind(MediaEngine, out var kind))
        {
            return kind;
        }

        return EdgeProxyMediaEngineKind.None;
    }

    /// <summary>
    /// Resolves engine switch strategy mode.
    /// </summary>
    public EdgeProxyEngineSwitchMode GetEngineSwitchMode()
    {
        if (TryParseEngineSwitchMode(EngineSwitchMode, out var mode))
        {
            return mode;
        }

        return EdgeProxyEngineSwitchMode.DefaultSwitch;
    }

    /// <summary>
    /// Validates that required connection and identity settings are present.
    /// </summary>
    /// <param name="error">Validation error message.</param>
    /// <returns><see langword="true"/> when options are valid; otherwise <see langword="false"/>.</returns>
    public bool IsValid(out string error)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            error = "BaseUrl must be an absolute URL.";
            return false;
        }

        if (!TryParseCommandExecutorKind(CommandExecutor, out var executorKind))
        {
            error = $"CommandExecutor '{CommandExecutor}' is not supported. Use 'controlws' or 'uart'.";
            return false;
        }

        if (!TryParseTransportEngineKind(TransportEngine, out _))
        {
            error = $"TransportEngine '{TransportEngine}' is not supported. Use 'relay' or 'dcd'.";
            return false;
        }

        if (!TryParseMediaEngineKind(MediaEngine, out _))
        {
            error = $"MediaEngine '{MediaEngine}' is not supported. Use 'none' or 'ffmpeg-dcd'.";
            return false;
        }

        if (!TryParseEngineSwitchMode(EngineSwitchMode, out _))
        {
            error = $"EngineSwitchMode '{EngineSwitchMode}' is not supported. Use 'fixed', 'shadow', 'canary', or 'default-switch'.";
            return false;
        }

        if (executorKind == EdgeProxyCommandExecutorKind.ControlWs &&
            !Uri.TryCreate(ControlWsUrl, UriKind.Absolute, out _))
        {
            error = "ControlWsUrl must be an absolute URL.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MediaHealthUrl) &&
            !Uri.TryCreate(MediaHealthUrl, UriKind.Absolute, out _))
        {
            error = "MediaHealthUrl must be an absolute URL.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MediaWhipUrl) &&
            !Uri.TryCreate(MediaWhipUrl, UriKind.Absolute, out _))
        {
            error = "MediaWhipUrl must be an absolute URL.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MediaWhepUrl) &&
            !Uri.TryCreate(MediaWhepUrl, UriKind.Absolute, out _))
        {
            error = "MediaWhepUrl must be an absolute URL.";
            return false;
        }

        if (MediaBackendAutoStart && string.IsNullOrWhiteSpace(MediaBackendExecutablePath))
        {
            error = "MediaBackendExecutablePath is required when MediaBackendAutoStart=true.";
            return false;
        }

        if (executorKind == EdgeProxyCommandExecutorKind.UartHid && string.IsNullOrWhiteSpace(UartPort))
        {
            error = "UartPort is required when CommandExecutor='uart'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SessionId))
        {
            error = "SessionId is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PeerId))
        {
            error = "PeerId is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(EndpointId))
        {
            error = "EndpointId is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Parses engine switch strategy mode from aliases.
    /// </summary>
    private static bool TryParseEngineSwitchMode(string? value, out EdgeProxyEngineSwitchMode mode)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "":
            case "default":
            case "defaultswitch":
            case "production":
                mode = EdgeProxyEngineSwitchMode.DefaultSwitch;
                return true;
            case "fixed":
                mode = EdgeProxyEngineSwitchMode.Fixed;
                return true;
            case "shadow":
                mode = EdgeProxyEngineSwitchMode.Shadow;
                return true;
            case "canary":
                mode = EdgeProxyEngineSwitchMode.Canary;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>
    /// Parses executor value from configuration aliases.
    /// </summary>
    private static bool TryParseCommandExecutorKind(string? value, out EdgeProxyCommandExecutorKind kind)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "":
            case "uart":
            case "uarthid":
            case "serial":
                kind = EdgeProxyCommandExecutorKind.UartHid;
                return true;
            case "controlws":
            case "websocket":
                kind = EdgeProxyCommandExecutorKind.ControlWs;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Parses control transport engine from configuration aliases.
    /// </summary>
    private static bool TryParseTransportEngineKind(string? value, out EdgeProxyTransportEngineKind kind)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "":
            case "relay":
            case "relaycompat":
            case "compat":
                kind = EdgeProxyTransportEngineKind.RelayCompat;
                return true;
            case "dcd":
            case "datachanneldotnet":
                kind = EdgeProxyTransportEngineKind.DataChannelDotNet;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Parses media engine mode from configuration aliases.
    /// </summary>
    private static bool TryParseMediaEngineKind(string? value, out EdgeProxyMediaEngineKind kind)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "":
            case "none":
            case "disabled":
                kind = EdgeProxyMediaEngineKind.None;
                return true;
            case "ffmpegdcd":
            case "ffmpegdatachanneldotnet":
            case "dcdffmpeg":
                kind = EdgeProxyMediaEngineKind.FfmpegDataChannelDotNet;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
