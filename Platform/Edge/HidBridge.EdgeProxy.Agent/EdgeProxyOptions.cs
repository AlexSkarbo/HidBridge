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
    /// Selects command execution path: <c>controlws</c> (default) or <c>uart</c>.
    /// </summary>
    public string CommandExecutor { get; set; } = "controlws";

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

    public string PrincipalId { get; set; } = "smoke-runner";
    public string TenantId { get; set; } = "local-tenant";
    public string OrganizationId { get; set; } = "local-org";

    public string AccessToken { get; set; } = "";
    public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
    public string KeycloakRealm { get; set; } = "hidbridge-dev";
    public string TokenClientId { get; set; } = "controlplane-smoke";
    public string TokenUsername { get; set; } = "operator.smoke.admin";
    public string TokenPassword { get; set; } = "ChangeMe123!";

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

        return EdgeProxyCommandExecutorKind.ControlWs;
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
            case "controlws":
            case "websocket":
                kind = EdgeProxyCommandExecutorKind.ControlWs;
                return true;
            case "uart":
            case "uarthid":
            case "serial":
                kind = EdgeProxyCommandExecutorKind.UartHid;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
