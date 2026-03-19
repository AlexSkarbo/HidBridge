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
}
