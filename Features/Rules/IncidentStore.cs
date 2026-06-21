using System.IO;
using Microsoft.Data.Sqlite;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// SQLite-backed incident store, encrypted at rest with SQLite3MC (same cipher and
/// key approach as the history DB). One row per open incident, keyed by
/// (rule_id, key). Path + password are injected so it's test-friendly.
/// </summary>
public sealed class IncidentStore : IIncidentStore
{
    private readonly string _dbPath;
    private readonly string _password;

    public IncidentStore(string dbPath, string password)
    {
        _dbPath = dbPath;
        _password = password;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS incidents (
                rule_id         TEXT    NOT NULL,
                key             TEXT    NOT NULL,
                open_message_id TEXT    NOT NULL,
                opened_at       INTEGER NOT NULL,
                PRIMARY KEY (rule_id, key)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Incident? FindOpen(string ruleId, string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT open_message_id, opened_at FROM incidents
             WHERE rule_id = @rid AND key = @key LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new Incident(ruleId, key, reader.GetString(0), reader.GetInt64(1));
    }

    public void Open(string ruleId, string key, string messageId, long openedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO incidents (rule_id, key, open_message_id, opened_at)
            VALUES (@rid, @key, @mid, @at)
            ON CONFLICT(rule_id, key) DO UPDATE SET
                open_message_id = excluded.open_message_id,
                opened_at       = excluded.opened_at
            """;
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@mid", messageId);
        cmd.Parameters.AddWithValue("@at", openedAt);
        cmd.ExecuteNonQuery();
    }

    public void Resolve(string ruleId, string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM incidents WHERE rule_id = @rid AND key = @key";
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
        }.ToString());
        conn.Open();
        // PRAGMA key must be the first statement on the connection (see HistoryRepository).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA key = '" + _password.Replace("'", "''") + "'";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
