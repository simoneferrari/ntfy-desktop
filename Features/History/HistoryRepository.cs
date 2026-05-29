using System.IO;
using Microsoft.Data.Sqlite;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.History;

public class HistoryRepository
{
    private readonly string _dbPath;

    public event EventHandler<HistoryMessage>? MessageInserted;

    /// <summary>Fires after rows are removed (single, by-topic, all, or a retention
    /// sweep). Lets unread-count consumers re-sync without coupling to each caller.</summary>
    public event EventHandler? HistoryChanged;

    public HistoryRepository()
    {
        Directory.CreateDirectory(App.DataPath);
        _dbPath = Path.Combine(App.DataPath, "history.db");
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id  TEXT    NOT NULL UNIQUE,
                topic       TEXT    NOT NULL,
                topic_id    TEXT,
                server_id   TEXT,
                timestamp   INTEGER NOT NULL,
                priority    INTEGER NOT NULL DEFAULT 3,
                title       TEXT,
                body        TEXT,
                tags        TEXT,
                click       TEXT,
                read        INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp ON messages(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_topic     ON messages(topic);
            """;
        cmd.ExecuteNonQuery();

        // Migrate older databases that predate the topic_id / server_id columns.
        // Must run before any index that references those columns, otherwise an
        // existing table (kept as-is by CREATE TABLE IF NOT EXISTS) lacks them.
        EnsureColumn(conn, "topic_id");
        EnsureColumn(conn, "server_id");

        // Unread tracking (0 = unread, 1 = read). When the column is added to an
        // existing database, mark all pre-existing rows read so the user doesn't
        // get flooded with unread badges for messages they've already seen — only
        // messages arriving after the upgrade count as unread.
        if (EnsureColumn(conn, "read", "INTEGER NOT NULL DEFAULT 0"))
        {
            using var markRead = conn.CreateCommand();
            markRead.CommandText = "UPDATE messages SET read = 1";
            markRead.ExecuteNonQuery();
        }

        using var idx = conn.CreateCommand();
        idx.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_topic_id ON messages(topic_id);
            CREATE INDEX IF NOT EXISTS idx_unread   ON messages(topic_id) WHERE read = 0;
            """;
        idx.ExecuteNonQuery();
    }

    /// <summary>Adds the column if missing. Returns true when it was actually added.</summary>
    private static bool EnsureColumn(SqliteConnection conn, string column, string definition = "TEXT")
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = @c";
        check.Parameters.AddWithValue("@c", column);
        var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (exists) return false;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE messages ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// One-time migration: stamps topic_id onto pre-existing rows by matching the
    /// stored topic name to a (name → id) map. Safe because topic names were unique
    /// before multi-server existed. Only touches rows that don't already have an id.
    /// </summary>
    public void BackfillTopicIds(IEnumerable<(string Name, Guid Id, Guid ServerId)> topics)
    {
        using var conn = Open();
        foreach (var (name, id, serverId) in topics)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE messages
                   SET topic_id = @id, server_id = @sid
                 WHERE topic = @name AND (topic_id IS NULL OR topic_id = '')
                """;
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@sid", serverId.ToString());
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }
    }

    public void Insert(NtfyMessage message, Guid topicId, Guid serverId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages
                (message_id, topic, topic_id, server_id, timestamp, priority, title, body, tags, click)
            VALUES
                (@mid, @topic, @topicId, @serverId, @ts, @priority, @title, @body, @tags, @click)
            """;
        cmd.Parameters.AddWithValue("@mid", message.Id);
        cmd.Parameters.AddWithValue("@topic", message.Topic);
        cmd.Parameters.AddWithValue("@topicId", topicId.ToString());
        cmd.Parameters.AddWithValue("@serverId", serverId.ToString());
        cmd.Parameters.AddWithValue("@ts", message.Time);
        cmd.Parameters.AddWithValue("@priority", (int)message.Priority);
        cmd.Parameters.AddWithValue("@title", (object?)message.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@body", (object?)message.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags",
            (object?)(message.Tags?.Count > 0 ? string.Join(",", message.Tags) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@click", (object?)message.Click ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        var histMsg = ToHistoryMessage(message, topicId);
        MessageInserted?.Invoke(this, histMsg);

        // Retention sweeps run on a timer in HistoryRetentionService, not per-Insert.
    }

    public List<HistoryMessage> Query(
        Guid? topicId = null,
        Priority? minPriority = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (topicId != null) { conditions.Add("topic_id = @topicId"); cmd.Parameters.AddWithValue("@topicId", topicId.Value.ToString()); }
        if (minPriority != null) { conditions.Add("priority >= @minP"); cmd.Parameters.AddWithValue("@minP", (int)minPriority.Value); }
        if (from != null) { conditions.Add("timestamp >= @from"); cmd.Parameters.AddWithValue("@from", from.Value.ToUnixTimeSeconds()); }
        if (to != null) { conditions.Add("timestamp <= @to"); cmd.Parameters.AddWithValue("@to", to.Value.ToUnixTimeSeconds()); }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT * FROM messages {where} ORDER BY timestamp DESC LIMIT {limit}";

        var results = new List<HistoryMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadRow(reader));

        return results;
    }

    /// <summary>
    /// Unread (read = 0) message counts grouped by topic id. Rows whose topic_id is
    /// null/blank (pre-backfill orphans) bucket under <see cref="Guid.Empty"/>; they
    /// still appear in the All-topics feed, so counting them keeps the total honest.
    /// </summary>
    public Dictionary<Guid, int> GetUnreadCounts()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT topic_id, COUNT(*) FROM messages WHERE read = 0 GROUP BY topic_id";

        var counts = new Dictionary<Guid, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) || !Guid.TryParse(reader.GetString(0), out var g) ? Guid.Empty : g;
            counts[id] = counts.GetValueOrDefault(id) + reader.GetInt32(1);
        }
        return counts;
    }

    public void MarkTopicRead(Guid topicId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET read = 1 WHERE topic_id = @id AND read = 0";
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void MarkAllRead()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET read = 1 WHERE read = 0";
        cmd.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages";
        RaiseIfDeleted(cmd.ExecuteNonQuery());
    }

    public void DeleteByTopicId(Guid topicId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE topic_id = @id";
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        RaiseIfDeleted(cmd.ExecuteNonQuery());
    }

    public void DeleteByRowId(long rowId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", rowId);
        RaiseIfDeleted(cmd.ExecuteNonQuery());
    }

    public void DeleteOlderThan(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        RaiseIfDeleted(cmd.ExecuteNonQuery());
    }

    private void RaiseIfDeleted(int rowsAffected)
    {
        if (rowsAffected > 0)
            HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static HistoryMessage ReadRow(SqliteDataReader r)
    {
        int Col(string name) => r.GetOrdinal(name);
        string? NullStr(string name) => r.IsDBNull(Col(name)) ? null : r.GetString(Col(name));

        return new HistoryMessage
        {
            RowId = r.GetInt64(Col("id")),
            MessageId = r.GetString(Col("message_id")),
            Topic = r.GetString(Col("topic")),
            TopicId = Guid.TryParse(NullStr("topic_id"), out var tid) ? tid : Guid.Empty,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(Col("timestamp"))),
            Priority = (Priority)r.GetInt32(Col("priority")),
            Title = NullStr("title"),
            Body = NullStr("body"),
            Tags = NullStr("tags"),
            Click = NullStr("click"),
        };
    }

    private static HistoryMessage ToHistoryMessage(NtfyMessage m, Guid topicId) => new()
    {
        MessageId = m.Id,
        Topic = m.Topic,
        TopicId = topicId,
        Timestamp = m.Timestamp,
        Priority = m.Priority,
        Title = m.Title,
        Body = m.Message,
        Tags = m.Tags?.Count > 0 ? string.Join(",", m.Tags) : null,
        Click = m.Click,
    };
}
