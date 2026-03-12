namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Defines authorization policy names used by the operator web shell.
/// </summary>
public static class OperatorPolicies
{
    /// <summary>
    /// Policy required to view operator dashboards.
    /// </summary>
    public const string Viewer = "OperatorViewer";

    /// <summary>
    /// Policy required to moderate invitations and control leases.
    /// </summary>
    public const string Moderator = "OperatorModerator";

    /// <summary>
    /// Policy required for administrative operations.
    /// </summary>
    public const string Admin = "OperatorAdmin";
}
