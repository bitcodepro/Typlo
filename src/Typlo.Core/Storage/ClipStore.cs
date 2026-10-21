using System.Text;
using Microsoft.Data.Sqlite;

namespace Typlo.Core.Storage;

public enum ClipKind
{
    Text = 1,
    Image = 2,
}

/// <summary>New clipboard content to store. Images come as PNG plus a small PNG thumbnail.</summary>
public sealed record ClipContent(ClipKind Kind, string? Text, byte[]? ImagePng, byte[]? Thumbnail,
    int Width = 0, int Height = 0, string? SourceApp = null)
{
    public static ClipContent FromText(string text, string? app = null) => new(ClipKind.Text, text, null, null, SourceApp: app);

    public static ClipContent FromImage(byte[] png, byte[] thumbnail, int width, int height, string? app = null) =>
        new(ClipKind.Image, null, png, thumbnail, width, height, app);

    public byte[] PayloadBytes() => Kind == ClipKind.Text ? Encoding.UTF8.GetBytes(Text ?? "") : ImagePng ?? [];
}

/// <summary>Decrypted metadata kept in memory for the history window; payloads load on demand.</summary>
public sealed class ClipEntry
{
    public const int PreviewChars = 400;

    public long Id { get; init; }
    public ClipKind Kind { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset LastUsed { get; set; }
    public bool Pinned { get; set; }
    public long Size { get; init; }
    /// <summary>Start of the text (text clips) — also used for search.</summary>
    public string PreviewText { get; init; } = "";
    /// <summary>PNG thumbnail (image clips).</summary>
    public byte[]? Thumbnail { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? SourceApp { get; init; }
}

public sealed record StoreLimits(int MaxItems, long MaxTotalBytes, long MaxImageBytes, long MaxTextBytes);

/// <summary>
/// Encrypted clipboard history in a single SQLite file. Everything that reveals content
/// (text, images, thumbnails, source app) is AES-GCM encrypted; the de-dup hash is keyed.
/// Thread-safe: all calls are serialized.
/// </summary>
public sealed class ClipStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ClipCrypto _crypto;
    private readonly Lock _lock = new();
    private StoreLimits _limits;

    public event Action? Changed;

    public ClipStore(string dbPath, ClipCrypto crypto, StoreLimits limits)
    {
        _crypto = crypto;
        _limits = limits;
        if (dbPath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              kind INTEGER NOT NULL,
              created INTEGER NOT NULL,
              last_used INTEGER NOT NULL,
              pinned INTEGER NOT NULL DEFAULT 0,
              hash BLOB NOT NULL UNIQUE,
              size INTEGER NOT NULL,
              width INTEGER NOT NULL DEFAULT 0,
              height INTEGER NOT NULL DEFAULT 0,
              preview BLOB NOT NULL,
              app BLOB,
              payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_clips_last_used ON clips(last_used);
            """);
    }

    public void UpdateLimits(StoreLimits limits)
    {
        lock (_lock) { _limits = limits; Enforce(); }
        Changed?.Invoke();
    }

    /// <summary>Stores content or bumps an identical existing clip to the top. Returns null if rejected by limits.</summary>
    public long? Add(ClipContent content)
    {
        var payload = content.PayloadBytes();
        if (payload.Length == 0) return null;
        if (content.Kind == ClipKind.Text && (payload.Length > _limits.MaxTextBytes || string.IsNullOrWhiteSpace(content.Text)))
            return null;
        if (content.Kind == ClipKind.Image && payload.Length > _limits.MaxImageBytes) return null;

        long id;
        lock (_lock) id = AddLocked(content, payload);
        Changed?.Invoke();
        return id;
    }

    private long AddLocked(ClipContent content, byte[] payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hash = _crypto.Hash(content.Kind, payload);
        using (var find = Cmd("SELECT id FROM clips WHERE hash=$h", ("$h", hash)))
        {
            if (find.ExecuteScalar() is long existing)
            {
                using var bump = Cmd("UPDATE clips SET last_used=$t WHERE id=$id", ("$t", now), ("$id", existing));
                bump.ExecuteNonQuery();
                return existing;
            }
        }

        {
            var preview = content.Kind == ClipKind.Text
                ? _crypto.EncryptString(Truncate(content.Text!, ClipEntry.PreviewChars))
                : _crypto.Encrypt(content.Thumbnail ?? []);
            using var insert = Cmd("""
                INSERT INTO clips(kind, created, last_used, pinned, hash, size, width, height, preview, app, payload)
                VALUES($k, $t, $t, 0, $h, $s, $w, $hh, $p, $a, $d) RETURNING id
                """,
                ("$k", (int)content.Kind), ("$t", now), ("$h", hash), ("$s", payload.LongLength),
                ("$w", content.Width), ("$hh", content.Height), ("$p", preview),
                ("$a", content.SourceApp is null ? DBNull.Value : _crypto.EncryptString(content.SourceApp)),
                ("$d", _crypto.Encrypt(payload)));
            var id = (long)insert.ExecuteScalar()!;
            Enforce();
            return id;
        }
    }

    public IReadOnlyList<ClipEntry> List()
    {
        var list = new List<ClipEntry>();
        lock (_lock)
        {
            using var cmd = Cmd("""
                SELECT id, kind, created, last_used, pinned, size, width, height, preview, app
                FROM clips ORDER BY pinned DESC, last_used DESC, id DESC
                """);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = (ClipKind)r.GetInt32(1);
                var preview = (byte[])r[8];
                try
                {
                    list.Add(new ClipEntry
                    {
                        Id = r.GetInt64(0),
                        Kind = kind,
                        Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                        LastUsed = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                        Pinned = r.GetInt64(4) != 0,
                        Size = r.GetInt64(5),
                        Width = r.GetInt32(6),
                        Height = r.GetInt32(7),
                        PreviewText = kind == ClipKind.Text ? _crypto.DecryptString(preview) : "",
                        Thumbnail = kind == ClipKind.Image ? _crypto.Decrypt(preview) : null,
                        SourceApp = r.IsDBNull(9) ? null : _crypto.DecryptString((byte[])r[9]),
                    });
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Written with another key (e.g. key file replaced): unreadable, skip it.
                }
            }
        }
        return list;
    }

    /// <summary>Full decrypted content of a clip, or null if it no longer exists.</summary>
    public ClipContent? Get(long id)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT kind, payload, width, height FROM clips WHERE id=$id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var kind = (ClipKind)r.GetInt32(0);
            var data = _crypto.Decrypt((byte[])r[1]);
            return kind == ClipKind.Text
                ? ClipContent.FromText(Encoding.UTF8.GetString(data))
                : new ClipContent(ClipKind.Image, null, data, null, r.GetInt32(2), r.GetInt32(3));
        }
    }

    public void Touch(long id)
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE clips SET last_used=$t WHERE id=$id",
                ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke();
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE clips SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
            cmd.ExecuteNonQuery();
            if (!pinned) Enforce();
        }
        Changed?.Invoke();
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = Cmd("DELETE FROM clips WHERE id=$id", ("$id", id));
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke();
    }

    /// <summary>Removes all unpinned clips (or everything when <paramref name="includePinned"/>).</summary>
    public void Clear(bool includePinned = false)
    {
        lock (_lock)
        {
            Exec(includePinned ? "DELETE FROM clips" : "DELETE FROM clips WHERE pinned=0");
            Exec("VACUUM");
        }
        Changed?.Invoke();
    }

    public (int Count, long TotalBytes) Stats()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT COUNT(*), COALESCE(SUM(size),0) FROM clips");
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt64(1));
        }
    }

    /// <summary>Evicts the least recently used unpinned clips until count and size fit the limits.</summary>
    private void Enforce()
    {
        while (true)
        {
            using var stats = Cmd("SELECT COUNT(*), COALESCE(SUM(size),0) FROM clips");
            using var r = stats.ExecuteReader();
            r.Read();
            var count = r.GetInt32(0);
            var total = r.GetInt64(1);
            r.Close();
            if (count <= _limits.MaxItems && total <= _limits.MaxTotalBytes) return;

            using var victim = Cmd("SELECT id FROM clips WHERE pinned=0 ORDER BY last_used ASC, id ASC LIMIT 1");
            if (victim.ExecuteScalar() is not long id) return; // only pinned left
            using var del = Cmd("DELETE FROM clips WHERE id=$id", ("$id", id));
            del.ExecuteNonQuery();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
