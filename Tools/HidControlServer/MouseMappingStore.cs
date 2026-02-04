using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace HidControlServer;

/// <summary>
/// Record for mouse mapping.
/// </summary>
/// <param name="DeviceId">DeviceId.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ReportDescHash">ReportDescHash.</param>
/// <param name="ButtonsCount">ButtonsCount.</param>
/// <param name="Mapping">Mapping.</param>
/// <param name="UpdatedAt">UpdatedAt.</param>
public sealed record MouseMappingRecord(
    string DeviceId,
    int Itf,
    string ReportDescHash,
    int ButtonsCount,
    JsonElement Mapping,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Stores mouse mapping data.
/// </summary>
public sealed class MouseMappingStore
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>
    /// Executes MouseMappingStore.
    /// </summary>
    /// <param name="connectionString">The connectionString.</param>
    public MouseMappingStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("connectionString is required", nameof(connectionString));
        _connectionString = connectionString;
        Initialize();
    }

    /// <summary>
    /// Initializes initialize.
    /// </summary>
    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS mouse_mapping (
  device_id TEXT PRIMARY KEY,
  itf INTEGER,
  report_desc_hash TEXT,
  buttons_count INTEGER,
  mapping_json JSONB NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL
);
""";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Opens open.
    /// </summary>
    /// <returns>Result.</returns>
    private NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Gets get.
    /// </summary>
    /// <param name="deviceId">The deviceId.</param>
    /// <returns>Result.</returns>
    public MouseMappingRecord? Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id,itf,report_desc_hash,buttons_count,mapping_json::text,updated_at FROM mouse_mapping WHERE device_id=@id";
        cmd.Parameters.AddWithValue("id", deviceId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string mappingJson = reader.GetString(4);
        using JsonDocument doc = JsonDocument.Parse(mappingJson);

        return new MouseMappingRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt32(3),
            doc.RootElement.Clone(),
            reader.GetDateTime(5));
    }

    /// <summary>
    /// Executes Upsert.
    /// </summary>
    /// <param name="record">The record.</param>
    public void Upsert(MouseMappingRecord record)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO mouse_mapping(device_id,itf,report_desc_hash,buttons_count,mapping_json,updated_at)
VALUES(@id,@itf,@hash,@count,@json,@updated)
ON CONFLICT(device_id) DO UPDATE SET
  itf=excluded.itf,
  report_desc_hash=excluded.report_desc_hash,
  buttons_count=excluded.buttons_count,
  mapping_json=excluded.mapping_json,
  updated_at=excluded.updated_at;
""";
        cmd.Parameters.AddWithValue("id", record.DeviceId);
        cmd.Parameters.AddWithValue("itf", record.Itf);
        cmd.Parameters.AddWithValue("hash", record.ReportDescHash);
        cmd.Parameters.AddWithValue("count", record.ButtonsCount);
        var mappingParam = new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = record.Mapping.GetRawText() };
        cmd.Parameters.Add(mappingParam);
        cmd.Parameters.AddWithValue("updated", record.UpdatedAt.UtcDateTime);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes ListAll.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<MouseMappingRecord> ListAll()
    {
        var list = new List<MouseMappingRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id,itf,report_desc_hash,buttons_count,mapping_json::text,updated_at FROM mouse_mapping ORDER BY updated_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string mappingJson = reader.GetString(4);
            using JsonDocument doc = JsonDocument.Parse(mappingJson);
            list.Add(new MouseMappingRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                doc.RootElement.Clone(),
                reader.GetDateTime(5)));
        }
        return list;
    }
}
