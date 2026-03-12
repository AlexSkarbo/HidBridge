namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Defines the logical OpenAPI/Scalar tags used to group Minimal API endpoints.
/// </summary>
public static class ApiEndpointTags
{
    /// <summary>
    /// Groups health, diagnostics, and runtime metadata endpoints.
    /// </summary>
    public const string System = "System";

    /// <summary>
    /// Groups inventory endpoints for agents and endpoints.
    /// </summary>
    public const string Inventory = "Inventory";

    /// <summary>
    /// Groups core session lifecycle and command dispatch endpoints.
    /// </summary>
    public const string Sessions = "Sessions";

    /// <summary>
    /// Groups collaboration-oriented participants, shares, and summaries.
    /// </summary>
    public const string Collaboration = "Collaboration";

    /// <summary>
    /// Groups collaboration-oriented read models and dashboard queries.
    /// </summary>
    public const string CollaborationReadModels = "Collaboration Read Models";

    /// <summary>
    /// Groups fleet, audit, and telemetry dashboard projections for operator consoles.
    /// </summary>
    public const string Dashboards = "Dashboards";

    /// <summary>
    /// Groups filterable and paged optimized query projections used by operator consoles and thin UI clients.
    /// </summary>
    public const string Projections = "Projections";

    /// <summary>
    /// Groups replay-oriented and archive-oriented diagnostics endpoints.
    /// </summary>
    public const string Diagnostics = "Diagnostics";

    /// <summary>
    /// Groups control-arbitration and active-controller lease endpoints.
    /// </summary>
    public const string Control = "Control";

    /// <summary>
    /// Groups audit and telemetry event stream endpoints.
    /// </summary>
    public const string Events = "Events";
}
