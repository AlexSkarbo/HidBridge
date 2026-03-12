namespace HidBridge.Persistence;

/// <summary>
/// Defines the file locations used by the P0-3 file-backed persistence baseline.
/// </summary>
public sealed class FilePersistenceOptions
{
    /// <summary>
    /// Initializes the persistence options with the specified root directory.
    /// </summary>
    /// <param name="rootDirectory">The root directory that stores all Platform snapshot files.</param>
    public FilePersistenceOptions(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>
    /// Gets the root directory that stores all persistence files.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Gets the connector descriptor snapshot file path.
    /// </summary>
    public string ConnectorsPath => Path.Combine(RootDirectory, "connectors.json");

    /// <summary>
    /// Gets the endpoint snapshot file path.
    /// </summary>
    public string EndpointsPath => Path.Combine(RootDirectory, "endpoints.json");

    /// <summary>
    /// Gets the session snapshot file path.
    /// </summary>
    public string SessionsPath => Path.Combine(RootDirectory, "sessions.json");

    /// <summary>
    /// Gets the audit event stream file path.
    /// </summary>
    public string AuditPath => Path.Combine(RootDirectory, "audit.json");

    /// <summary>
    /// Gets the telemetry event stream file path.
    /// </summary>
    public string TelemetryPath => Path.Combine(RootDirectory, "telemetry.json");

    /// <summary>
    /// Gets the command journal file path.
    /// </summary>
    public string CommandsPath => Path.Combine(RootDirectory, "commands.json");

    /// <summary>
    /// Gets the policy scope snapshot file path.
    /// </summary>
    public string PolicyScopesPath => Path.Combine(RootDirectory, "policy-scopes.json");

    /// <summary>
    /// Gets the policy assignment snapshot file path.
    /// </summary>
    public string PolicyAssignmentsPath => Path.Combine(RootDirectory, "policy-assignments.json");

    /// <summary>
    /// Gets the policy revision snapshot file path.
    /// </summary>
    public string PolicyRevisionsPath => Path.Combine(RootDirectory, "policy-revisions.json");
}
