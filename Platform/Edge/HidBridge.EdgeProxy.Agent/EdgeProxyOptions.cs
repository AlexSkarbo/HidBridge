using System.ComponentModel.DataAnnotations;

namespace HidBridge.EdgeProxy.Agent;

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

    [Required]
    public string ControlWsUrl { get; set; } = "ws://127.0.0.1:28092/ws/control";

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

    public void Normalize()
    {
        BaseUrl = BaseUrl.TrimEnd('/');
        PollIntervalMs = Math.Max(100, PollIntervalMs);
        BatchLimit = Math.Clamp(BatchLimit, 1, 500);
        HeartbeatIntervalSec = Math.Max(3, HeartbeatIntervalSec);
        CommandTimeoutMs = Math.Max(1000, CommandTimeoutMs);
        HttpTimeoutSec = Math.Max(5, HttpTimeoutSec);
        ReconnectBackoffMinMs = Math.Max(100, ReconnectBackoffMinMs);
        ReconnectBackoffMaxMs = Math.Max(ReconnectBackoffMinMs, ReconnectBackoffMaxMs);
        ReconnectBackoffJitterMs = Math.Max(0, ReconnectBackoffJitterMs);
        TransientFailureThresholdForOffline = Math.Max(1, TransientFailureThresholdForOffline);
    }

    public bool IsValid(out string error)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            error = "BaseUrl must be an absolute URL.";
            return false;
        }

        if (!Uri.TryCreate(ControlWsUrl, UriKind.Absolute, out _))
        {
            error = "ControlWsUrl must be an absolute URL.";
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
}
