namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Defines optional startup bootstrap settings for persisted operator policy configuration.
/// </summary>
public sealed class PolicyBootstrapOptions
{
    /// <summary>
    /// Gets or sets the default policy scope identifier.
    /// </summary>
    public required string ScopeId { get; init; }

    /// <summary>
    /// Gets or sets the tenant assigned to the default scope.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Gets or sets the organization assigned to the default scope.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether viewer access is required for scoped reads.
    /// </summary>
    public bool ViewerRoleRequired { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether moderator overrides are enabled.
    /// </summary>
    public bool ModeratorOverrideEnabled { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether admin overrides are enabled.
    /// </summary>
    public bool AdminOverrideEnabled { get; init; } = true;

    /// <summary>
    /// Gets the startup policy assignments keyed by principal identifier.
    /// </summary>
    public IReadOnlyList<PolicyBootstrapAssignment> Assignments { get; init; } = [];
}

/// <summary>
/// Represents one startup policy assignment entry.
/// </summary>
public sealed record PolicyBootstrapAssignment(
    string PrincipalId,
    IReadOnlyList<string> Roles,
    string? Source = null);
