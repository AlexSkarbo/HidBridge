using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace HidControlServer;

/// <summary>
/// Record for keyboard mapping.
/// </summary>
/// <param name="DeviceId">DeviceId.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ReportDescHash">ReportDescHash.</param>
/// <param name="Mapping">Mapping.</param>
/// <param name="UpdatedAt">UpdatedAt.</param>
public sealed record KeyboardMappingRecord(
    string DeviceId,
    byte Itf,
    string ReportDescHash,
    JsonElement Mapping,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Stores keyboard mapping data.
/// </summary>
public sealed class KeyboardMappingStore
{
    private readonly string _connectionString;

    /// <summary>
    /// Executes KeyboardMappingStore.
    /// </summary>
    /// <param name="connectionString">The connectionString.</param>
    public KeyboardMappingStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    /// <summary>
    /// Ensures schema.
    /// </summary>
    private void EnsureSchema()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS keyboard_mapping (
    device_id TEXT PRIMARY KEY,
    itf INTEGER NOT NULL,
    report_desc_hash TEXT NOT NULL,
    mapping_json JSONB NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
""";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets get.
    /// </summary>
    /// <param name="deviceId">The deviceId.</param>
    /// <returns>Result.</returns>
    public KeyboardMappingRecord? Get(string deviceId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, itf, report_desc_hash, mapping_json::text, updated_at FROM keyboard_mapping WHERE device_id = @id";
        cmd.Parameters.AddWithValue("id", deviceId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string mappingJson = reader.GetString(3);
        using JsonDocument doc = JsonDocument.Parse(mappingJson);
        JsonElement mappingClone = doc.RootElement.Clone();

        return new KeyboardMappingRecord(
            reader.GetString(0),
            (byte)reader.GetInt32(1),
            reader.GetString(2),
            mappingClone,
            reader.GetDateTime(4));
    }

    /// <summary>
    /// Executes Upsert.
    /// </summary>
    /// <param name="record">The record.</param>
    public void Upsert(KeyboardMappingRecord record)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO keyboard_mapping (device_id, itf, report_desc_hash, mapping_json, updated_at)
VALUES (@id, @itf, @hash, @mapping, @updated)
ON CONFLICT(device_id) DO UPDATE SET
    itf = excluded.itf,
    report_desc_hash = excluded.report_desc_hash,
    mapping_json = excluded.mapping_json,
    updated_at = excluded.updated_at;
""";
        cmd.Parameters.AddWithValue("id", record.DeviceId);
        cmd.Parameters.AddWithValue("itf", record.Itf);
        cmd.Parameters.AddWithValue("hash", record.ReportDescHash);
        var mappingParam = new NpgsqlParameter("mapping", NpgsqlDbType.Jsonb) { Value = record.Mapping.GetRawText() };
        cmd.Parameters.Add(mappingParam);
        cmd.Parameters.AddWithValue("updated", record.UpdatedAt.UtcDateTime);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes ListAll.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<KeyboardMappingRecord> ListAll()
    {
        var list = new List<KeyboardMappingRecord>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, itf, report_desc_hash, mapping_json::text, updated_at FROM keyboard_mapping ORDER BY updated_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string mappingJson = reader.GetString(3);
            using JsonDocument doc = JsonDocument.Parse(mappingJson);
            JsonElement mappingClone = doc.RootElement.Clone();
            list.Add(new KeyboardMappingRecord(
                reader.GetString(0),
                (byte)reader.GetInt32(1),
                reader.GetString(2),
                mappingClone,
                reader.GetDateTime(4)));
        }
        return list;
    }
}
