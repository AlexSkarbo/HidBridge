namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Defines custom claim types used by the operator web shell.
/// </summary>
public static class OperatorClaimTypes
{
    /// <summary>
    /// Claim type for the tenant identifier.
    /// </summary>
    public const string TenantId = "tenant_id";

    /// <summary>
    /// Claim type for the organization identifier.
    /// </summary>
    public const string OrganizationId = "org_id";

    /// <summary>
    /// Claim type for the operator principal identifier used by control actions.
    /// </summary>
    public const string PrincipalId = "principal_id";
}
