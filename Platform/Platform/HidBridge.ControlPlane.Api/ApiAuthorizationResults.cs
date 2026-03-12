namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Structured authorization denial details returned by the API.
/// </summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Code">Stable machine-readable denial code.</param>
/// <param name="Message">Human-readable denial message.</param>
/// <param name="SessionId">Optional affected session identifier.</param>
/// <param name="ShareId">Optional affected share identifier.</param>
/// <param name="ParticipantId">Optional affected participant identifier.</param>
/// <param name="RequiredRoles">Optional required operator roles.</param>
/// <param name="RequiredTenantId">Optional required tenant scope.</param>
/// <param name="RequiredOrganizationId">Optional required organization scope.</param>
/// <param name="Caller">Projected caller context for diagnostics.</param>
public sealed record AuthorizationDeniedBody(
    int Status,
    string Code,
    string Message,
    string? SessionId,
    string? ShareId,
    string? ParticipantId,
    IReadOnlyList<string>? RequiredRoles,
    string? RequiredTenantId,
    string? RequiredOrganizationId,
    AuthorizationDeniedCallerBody Caller);

/// <summary>
/// Projected caller metadata included in authorization denial responses.
/// </summary>
/// <param name="SubjectId">Caller subject identifier.</param>
/// <param name="PrincipalId">Caller principal identifier.</param>
/// <param name="TenantId">Caller tenant identifier.</param>
/// <param name="OrganizationId">Caller organization identifier.</param>
/// <param name="Roles">Caller operator roles.</param>
public sealed record AuthorizationDeniedCallerBody(
    string? SubjectId,
    string? PrincipalId,
    string? TenantId,
    string? OrganizationId,
    IReadOnlyList<string> Roles);

/// <summary>
/// Authorization exception with stable denial metadata.
/// </summary>
public sealed class ApiAuthorizationException : UnauthorizedAccessException
{
    /// <summary>
    /// Initializes a new authorization exception.
    /// </summary>
    public ApiAuthorizationException(
        string code,
        string message,
        IReadOnlyList<string>? requiredRoles = null,
        string? requiredTenantId = null,
        string? requiredOrganizationId = null)
        : base(message)
    {
        Code = code;
        RequiredRoles = requiredRoles;
        RequiredTenantId = requiredTenantId;
        RequiredOrganizationId = requiredOrganizationId;
    }

    /// <summary>
    /// Gets the stable machine-readable denial code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets any required operator roles for the denied action.
    /// </summary>
    public IReadOnlyList<string>? RequiredRoles { get; }

    /// <summary>
    /// Gets the tenant identifier required by the protected resource.
    /// </summary>
    public string? RequiredTenantId { get; }

    /// <summary>
    /// Gets the organization identifier required by the protected resource.
    /// </summary>
    public string? RequiredOrganizationId { get; }
}

/// <summary>
/// Produces structured authorization denial responses.
/// </summary>
public static class ApiAuthorizationResults
{
    /// <summary>
    /// Creates an unauthorized JSON result for one denied API action.
    /// </summary>
    public static IResult Unauthorized(
        ApiCallerContext caller,
        UnauthorizedAccessException exception,
        string? sessionId = null,
        string? shareId = null,
        string? participantId = null)
        => BuildResult(StatusCodes.Status401Unauthorized, caller, exception, sessionId, shareId, participantId);

    /// <summary>
    /// Creates a forbidden JSON result for one denied API action.
    /// </summary>
    public static IResult Forbidden(
        ApiCallerContext caller,
        UnauthorizedAccessException exception,
        string? sessionId = null,
        string? shareId = null,
        string? participantId = null)
    {
        return BuildResult(StatusCodes.Status403Forbidden, caller, exception, sessionId, shareId, participantId);
    }

    private static IResult BuildResult(
        int statusCode,
        ApiCallerContext caller,
        UnauthorizedAccessException exception,
        string? sessionId,
        string? shareId,
        string? participantId)
    {
        var authorizationException = exception as ApiAuthorizationException;
        var body = new AuthorizationDeniedBody(
            statusCode,
            authorizationException?.Code ?? "authorization_denied",
            exception.Message,
            sessionId,
            shareId,
            participantId,
            authorizationException?.RequiredRoles,
            authorizationException?.RequiredTenantId,
            authorizationException?.RequiredOrganizationId,
            new AuthorizationDeniedCallerBody(
                caller.SubjectId,
                caller.EffectivePrincipalId,
                caller.TenantId,
                caller.OrganizationId,
                caller.OperatorRoles));

        return Results.Json(body, statusCode: statusCode);
    }
}
