namespace HidBridge.Persistence.Sql;

/// <summary>
/// Defines the SQL persistence configuration used by the PostgreSQL-backed provider.
/// </summary>
public sealed class SqlPersistenceOptions
{
    /// <summary>
    /// Initializes the SQL persistence options.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string used by the Platform database.</param>
    /// <param name="schema">The PostgreSQL schema that stores Platform tables.</param>
    public SqlPersistenceOptions(string connectionString, string schema = "hidbridge")
    {
        ConnectionString = connectionString;
        Schema = schema;
    }

    /// <summary>
    /// Gets the PostgreSQL connection string used by the Platform database.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Gets the target PostgreSQL schema name.
    /// </summary>
    public string Schema { get; }
}
