using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HidControlServer;

/// <summary>
/// Migrates sqlite mapping data.
/// </summary>
public static class SqliteMappingMigrator
{
    /// <summary>
    /// Executes MigrateAsync.
    /// </summary>
    /// <param name="sqlitePath">The sqlitePath.</param>
    /// <param name="mouseStore">The mouseStore.</param>
    /// <param name="keyboardStore">The keyboardStore.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public static async Task MigrateAsync(string sqlitePath, MouseMappingStore mouseStore, KeyboardMappingStore keyboardStore, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath)) return;
        if (!File.Exists(sqlitePath)) return;

        await Task.Run(() =>
        {
            MigrateMouse(sqlitePath, mouseStore);
            MigrateKeyboard(sqlitePath, keyboardStore);
        }, ct);
    }

    /// <summary>
    /// Executes MigrateMouse.
    /// </summary>
    /// <param name="sqlitePath">The sqlitePath.</param>
    /// <param name="store">The store.</param>
    private static void MigrateMouse(string sqlitePath, MouseMappingStore store)
    {
        using var conn = new SqliteConnection($"Data Source={sqlitePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id,itf,report_desc_hash,buttons_count,mapping_json,updated_at FROM mouse_mapping";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string mappingJson = reader.GetString(4);
            using JsonDocument doc = JsonDocument.Parse(mappingJson);
            var record = new MouseMappingRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                doc.RootElement.Clone(),
                DateTimeOffset.Parse(reader.GetString(5)));
            store.Upsert(record);
        }
    }

    /// <summary>
    /// Executes MigrateKeyboard.
    /// </summary>
    /// <param name="sqlitePath">The sqlitePath.</param>
    /// <param name="store">The store.</param>
    private static void MigrateKeyboard(string sqlitePath, KeyboardMappingStore store)
    {
        using var conn = new SqliteConnection($"Data Source={sqlitePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id,itf,report_desc_hash,mapping_json,updated_at FROM keyboard_mapping";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string mappingJson = reader.GetString(3);
            using JsonDocument doc = JsonDocument.Parse(mappingJson);
            var record = new KeyboardMappingRecord(
                reader.GetString(0),
                (byte)reader.GetInt32(1),
                reader.GetString(2),
                doc.RootElement.Clone(),
                DateTimeOffset.Parse(reader.GetString(4)));
            store.Upsert(record);
        }
    }
}
