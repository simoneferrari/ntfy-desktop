using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History.Events;

namespace NtfyDesktop.Features.History;

public class HistoryRepository
{
    private readonly string _dbPath;

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

        // Attachment metadata, stored as the raw ntfy "attachment" JSON object
        // ({ name, type, size, expires, url }). A single column keeps the migration
        // trivial and is forward-compatible if ntfy adds fields.
        EnsureColumn(conn, "attachment");

        // Action buttons, stored as the raw ntfy "actions" JSON array. Same single-column
        // approach as attachment — trivial migration, forward-compatible.
        EnsureColumn(conn, "actions");

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

        EnsureCursorTable(conn);
    }

    // Per-topic ntfy `since=` cursor: the last message id we've acknowledged from the
    // server for each topic. Deliberately a SEPARATE table from `messages` so it is
    // NOT affected by retention sweeps or user deletes — otherwise deleting/pruning a
    // topic's newest rows would rewind the cursor and the server would replay (resurrect)
    // those messages on the next reconnect. Advanced forward-only in Insert.
    private static void EnsureCursorTable(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='topic_cursor'";
        var alreadyExists = Convert.ToInt64(check.ExecuteScalar()) > 0;

        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS topic_cursor (
                topic_id    TEXT    PRIMARY KEY,
                message_id  TEXT    NOT NULL,
                time        INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        if (alreadyExists) return;

        // First creation: seed each topic's cursor from its newest existing message so
        // current installs get catch-up immediately. SQLite takes the bare message_id
        // from the same row as MAX(timestamp). After this the cursor lives on its own.
        using var seed = conn.CreateCommand();
        seed.CommandText = """
            INSERT INTO topic_cursor (topic_id, message_id, time)
            SELECT topic_id, message_id, MAX(timestamp)
              FROM messages
             WHERE topic_id IS NOT NULL AND topic_id <> ''
             GROUP BY topic_id
            """;
        seed.ExecuteNonQuery();
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

    /// <summary>Stores a received message and advances the topic's catch-up cursor.
    /// Returns <c>true</c> only when the row was genuinely new (a <c>since=</c> catch-up
    /// re-delivers already-stored messages — callers use this to skip re-notifying).</summary>
    public bool Insert(NtfyMessage message, Guid topicId, Guid serverId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages
                (message_id, topic, topic_id, server_id, timestamp, priority, title, body, tags, click, attachment, actions)
            VALUES
                (@mid, @topic, @topicId, @serverId, @ts, @priority, @title, @body, @tags, @click, @attachment, @actions)
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
        cmd.Parameters.AddWithValue("@attachment", (object?)SerializeAttachment(message.Attachment) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actions", (object?)SerializeActions(message.Actions) ?? DBNull.Value);
        var inserted = cmd.ExecuteNonQuery() > 0; // INSERT OR IGNORE → 0 when message_id already stored

        // Advance the topic's catch-up cursor (forward only). Runs even when the row was
        // an IGNORE'd duplicate — we've still acknowledged seeing this id from the server.
        // Independent of retention/deletes, which never touch topic_cursor.
        AdvanceCursor(conn, topicId, message.Id, message.Time);

        // Only announce genuinely-new rows. A `since=<time>` catch-up is inclusive of its
        // boundary second, so it re-delivers already-stored messages; without this guard the
        // feed and unread count (neither dedupes on message id) would double-count them.
        if (!inserted) return false;

        var histMsg = ToHistoryMessage(message, topicId);
        _ = new MessageInserted(histMsg).PublishAsync();

        // Retention sweeps run on a timer in HistoryRetentionService, not per-Insert.
        return true;
    }

    /// <summary>
    /// Establishes a baseline catch-up cursor for a topic if it doesn't have one yet, so a
    /// topic is never cursorless on a reconnect (which would mean no <c>since=</c>, hence no
    /// catch-up). No-ops when a cursor already exists — never rewinds. Called at subscribe
    /// time with "now": a brand-new topic gets no backlog on its first connect, but every
    /// gap after that is caught.
    /// </summary>
    public void EnsureCursor(Guid topicId, long time)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO topic_cursor (topic_id, message_id, time)
            VALUES (@id, '', @t)
            ON CONFLICT(topic_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        cmd.Parameters.AddWithValue("@t", time);
        cmd.ExecuteNonQuery();
    }

    private static void AdvanceCursor(SqliteConnection conn, Guid topicId, string messageId, long time)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO topic_cursor (topic_id, message_id, time)
            VALUES (@id, @mid, @t)
            ON CONFLICT(topic_id) DO UPDATE SET
                message_id = excluded.message_id,
                time       = excluded.time
            WHERE excluded.time >= topic_cursor.time
            """;
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        cmd.Parameters.AddWithValue("@mid", messageId);
        cmd.Parameters.AddWithValue("@t", time);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The ntfy <c>since=</c> value for a topic — the Unix timestamp of the last message we
    /// acknowledged from the server — or null when we've never seen one. Sourced from the
    /// dedicated <c>topic_cursor</c> table, so it survives retention sweeps and user deletes
    /// (which only touch <c>messages</c>).
    ///
    /// We return the timestamp, not the message id, on purpose: <c>since=&lt;id&gt;</c> only
    /// works while that id is still in the server's cache (ntfy default ~12h) — once it ages
    /// out, ntfy can't locate it and returns <em>nothing</em>, so a topic that went quiet
    /// longer than the cache window would never catch up. <c>since=&lt;timestamp&gt;</c>
    /// degrades gracefully: an old value just yields everything still cached after it.
    /// </summary>
    public string? GetSinceValue(Guid topicId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT time FROM topic_cursor WHERE topic_id = @id";
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result).ToString(CultureInfo.InvariantCulture);
    }

#if DEBUG
    /// <summary>
    /// Dev/test aid: force the catch-up cursor of each given topic back to <paramref name="time"/>
    /// (use 0 to replay the server's whole cache on the next connect). Upserts a row even for
    /// topics that have none, so every configured topic re-fetches. Debug builds only.
    /// </summary>
    public void DevRewindCursors(IEnumerable<Guid> topicIds, long time)
    {
        using var conn = Open();
        foreach (var id in topicIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO topic_cursor (topic_id, message_id, time)
                VALUES (@id, '', @t)
                ON CONFLICT(topic_id) DO UPDATE SET time = @t
                """;
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@t", time);
            cmd.ExecuteNonQuery();
        }
    }
#endif

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
    /// A single stored message by its ntfy message id, or null when not found (e.g. retention
    /// pruned it). Used to resolve a toast action button back to its message + actions.
    /// </summary>
    public HistoryMessage? GetByMessageId(string messageId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE message_id = @mid LIMIT 1";
        cmd.Parameters.AddWithValue("@mid", messageId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
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

    public void DeleteAll(MessageDeletionSource source)
    {
        using var conn = Open();
        // Capture attachment URLs before the rows go, so their cache files can be dropped.
        var urls = SelectAttachmentUrls(conn, "1 = 1");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages";
        PublishIfDeleted(cmd.ExecuteNonQuery(), topicId: null, source, urls);
    }

    public void DeleteByTopicId(Guid topicId, MessageDeletionSource source)
    {
        using var conn = Open();
        var urls = SelectAttachmentUrls(conn, "topic_id = @id",
            c => c.Parameters.AddWithValue("@id", topicId.ToString()));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE topic_id = @id";
        cmd.Parameters.AddWithValue("@id", topicId.ToString());
        PublishIfDeleted(cmd.ExecuteNonQuery(), topicId, source, urls);
    }

    public void DeleteByRowId(long rowId, MessageDeletionSource source)
    {
        using var conn = Open();
        var urls = SelectAttachmentUrls(conn, "id = @id",
            c => c.Parameters.AddWithValue("@id", rowId));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", rowId);
        // Single-row delete spans no single topic — signal a broad change (null).
        PublishIfDeleted(cmd.ExecuteNonQuery(), topicId: null, source, urls);
    }

    public void DeleteOlderThan(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        using var conn = Open();
        var urls = SelectAttachmentUrls(conn, "timestamp < @cutoff",
            c => c.Parameters.AddWithValue("@cutoff", cutoff));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        PublishIfDeleted(cmd.ExecuteNonQuery(), topicId: null, MessageDeletionSource.Retention, urls);
    }

    // Attachment URLs of the rows matching a delete predicate, gathered just before the
    // DELETE so a consumer (the attachment cache) can purge their files. Empty when none.
    private static List<string> SelectAttachmentUrls(SqliteConnection conn, string predicate, Action<SqliteCommand>? bind = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT attachment FROM messages WHERE ({predicate}) AND attachment IS NOT NULL";
        bind?.Invoke(cmd);

        var urls = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = DeserializeAttachment(reader.IsDBNull(0) ? null : reader.GetString(0))?.Url;
            if (!string.IsNullOrEmpty(url)) urls.Add(url);
        }
        return urls;
    }

    // null TopicId = broad/unscoped deletion (all, retention, single row) → consumers
    // re-sync; a value = that topic's messages were removed wholesale. Source lets a
    // consumer ignore deletes it originated itself (the feed). urls = attachment URLs of
    // the removed rows, for cache cleanup.
    private static void PublishIfDeleted(int rowsAffected, Guid? topicId, MessageDeletionSource source,
        IReadOnlyList<string>? urls = null)
    {
        if (rowsAffected > 0)
            _ = new MessagesDeleted(topicId, source, urls).PublishAsync();
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
            Attachment = DeserializeAttachment(NullStr("attachment")),
            Actions = DeserializeActions(NullStr("actions")),
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
        Attachment = m.Attachment,
        Actions = m.Actions,
    };

    private static string? SerializeAttachment(NtfyAttachment? attachment) =>
        attachment is null ? null : JsonSerializer.Serialize(attachment);

    private static NtfyAttachment? DeserializeAttachment(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<NtfyAttachment>(json); }
        catch { return null; } // tolerate a malformed/legacy value rather than failing the read
    }

    private static string? SerializeActions(List<NtfyAction>? actions) =>
        actions is { Count: > 0 } ? JsonSerializer.Serialize(actions) : null;

    private static List<NtfyAction>? DeserializeActions(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<List<NtfyAction>>(json); }
        catch { return null; } // tolerate a malformed/legacy value rather than failing the read
    }
}
