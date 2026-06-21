using System.IO;
using Microsoft.Data.Sqlite;

namespace NtfyDesktop.Features.Rules;

public sealed record ExpectState(string RuleId, long LastSeenAt, Guid TopicId, bool Alerted);

/// <summary>
/// Per-expect-rule heartbeat state (last-seen time, the topic last seen on, and whether an
/// absence alert is outstanding). A second table in the encrypted rules.db, keyed by rule id.
/// </summary>
public sealed class ExpectationStore
{
    private readonly string _dbPath;
    private readonly string _password;

    public ExpectationStore(string dbPath, string password)
    {
        _dbPath = dbPath;
        _password = password;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS expectations (
                rule_id      TEXT    PRIMARY KEY,
                last_seen_at INTEGER NOT NULL,
                topic_id     TEXT    NOT NULL DEFAULT '',
                alerted      INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ExpectState? Get(string ruleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_seen_at, topic_id, alerted FROM expectations WHERE rule_id = @r LIMIT 1";
        cmd.Parameters.AddWithValue("@r", ruleId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var topicId = Guid.TryParse(reader.GetString(1), out var g) ? g : Guid.Empty;
        return new ExpectState(ruleId, reader.GetInt64(0), topicId, reader.GetInt64(2) != 0);
    }

    public void Seed(string ruleId, long lastSeenAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expectations (rule_id, last_seen_at, topic_id, alerted)
            VALUES (@r, @t, '', 0) ON CONFLICT(rule_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.Parameters.AddWithValue("@t", lastSeenAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upsert last-seen forward-only, set topic, clear alerted. Returns the
    /// previous alerted value so the caller can fire a recovery notice.</summary>
    public bool RecordSeen(string ruleId, long lastSeenAt, Guid topicId)
    {
        using var conn = Open();

        bool wasAlerted;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT alerted FROM expectations WHERE rule_id = @r LIMIT 1";
            read.Parameters.AddWithValue("@r", ruleId);
            var res = read.ExecuteScalar();
            wasAlerted = res is not (null or DBNull) && Convert.ToInt64(res) != 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expectations (rule_id, last_seen_at, topic_id, alerted)
            VALUES (@r, @t, @tid, 0)
            ON CONFLICT(rule_id) DO UPDATE SET
                last_seen_at = MAX(last_seen_at, excluded.last_seen_at),
                topic_id     = excluded.topic_id,
                alerted      = 0
            """;
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.Parameters.AddWithValue("@t", lastSeenAt);
        cmd.Parameters.AddWithValue("@tid", topicId.ToString());
        cmd.ExecuteNonQuery();
        return wasAlerted;
    }

    public void MarkAlerted(string ruleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE expectations SET alerted = 1 WHERE rule_id = @r";
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA key = '" + _password.Replace("'", "''") + "'";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
