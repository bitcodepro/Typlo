using System.Text;
using Microsoft.Data.Sqlite;

namespace Typlo.Core.Storage;

public enum ClipKind
{
    Text = 1,
    Image = 2,
    /// <summary>Files copied in a file manager; <see cref="ClipContent.Text"/> holds one path per line.</summary>
    Files = 3,
}

/// <summary>
/// New clipboard content to store. Images come as PNG plus a small PNG thumbnail. Text can carry the
/// formatted versions the source app offered (HTML, RTF) so pasting keeps tables, bold text, links.
/// </summary>
public sealed record ClipContent(ClipKind Kind, string? Text, byte[]? ImagePng, byte[]? Thumbnail,
    int Width = 0, int Height = 0, string? SourceApp = null, string? Html = null, string? Rtf = null)
{
    public static ClipContent FromText(string text, string? app = null, string? html = null, string? rtf = null) =>
        new(ClipKind.Text, text, null, null, SourceApp: app, Html: html, Rtf: rtf);

    public static ClipContent FromImage(byte[] png, byte[] thumbnail, int width, int height, string? app = null) =>
        new(ClipKind.Image, null, png, thumbnail, width, height, app);

    public static ClipContent FromFiles(IEnumerable<string> paths, string? app = null) =>
        new(ClipKind.Files, string.Join('\n', paths), null, null, SourceApp: app);

    public bool HasFormatting => !string.IsNullOrEmpty(Html) || !string.IsNullOrEmpty(Rtf);

    public IReadOnlyList<string> Files =>
        Kind == ClipKind.Files ? (Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    public byte[] PayloadBytes() => Kind == ClipKind.Image ? ImagePng ?? [] : Encoding.UTF8.GetBytes(Text ?? "");

    internal byte[]? FormattingBytes() => HasFormatting
        ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?> { ["html"] = Html, ["rtf"] = Rtf })
        : null;

    internal static (string? Html, string? Rtf) ParseFormatting(byte[] bytes)
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(bytes) ?? [];
        return (map.GetValueOrDefault("html"), map.GetValueOrDefault("rtf"));
    }
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
    /// <summary>The text also has HTML/RTF formatting.</summary>
    public bool HasFormatting { get; init; }
    public IReadOnlyList<string> Files => Kind == ClipKind.Files ? PreviewText.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
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
        // v0.1.5: encrypted HTML/RTF formatting; older databases get the column added.
        using (var columns = Cmd("SELECT COUNT(*) FROM pragma_table_info('clips') WHERE name='extra'"))
            if ((long)columns.ExecuteScalar()! == 0) Exec("ALTER TABLE clips ADD COLUMN extra BLOB");
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
        if (content.Kind is ClipKind.Text or ClipKind.Files && (payload.Length > _limits.MaxTextBytes || string.IsNullOrWhiteSpace(content.Text)))
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
        var formatting = content.FormattingBytes();
        object extra = formatting is null ? DBNull.Value : _crypto.Encrypt(formatting);
        using (var find = Cmd("SELECT id FROM clips WHERE hash=$h", ("$h", hash)))
        {
            if (find.ExecuteScalar() is long existing)
            {
                // Same text again: keep the newest formatting if the source offered any.
                using var bump = formatting is null
                    ? Cmd("UPDATE clips SET last_used=$t WHERE id=$id", ("$t", now), ("$id", existing))
                    : Cmd("UPDATE clips SET last_used=$t, extra=$x WHERE id=$id", ("$t", now), ("$x", extra), ("$id", existing));
                bump.ExecuteNonQuery();
                return existing;
            }
        }

        {
            var preview = content.Kind == ClipKind.Image
                ? _crypto.Encrypt(content.Thumbnail ?? [])
                : _crypto.EncryptString(Truncate(content.Text!, content.Kind == ClipKind.Files ? 4000 : ClipEntry.PreviewChars));
            using var insert = Cmd("""
                INSERT INTO clips(kind, created, last_used, pinned, hash, size, width, height, preview, app, payload, extra)
                VALUES($k, $t, $t, 0, $h, $s, $w, $hh, $p, $a, $d, $x) RETURNING id
                """,
                ("$k", (int)content.Kind), ("$t", now), ("$h", hash), ("$s", payload.LongLength + (formatting?.LongLength ?? 0)),
                ("$w", content.Width), ("$hh", content.Height), ("$p", preview),
                ("$a", content.SourceApp is null ? DBNull.Value : _crypto.EncryptString(content.SourceApp)),
                ("$d", _crypto.Encrypt(payload)), ("$x", extra));
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
                SELECT id, kind, created, last_used, pinned, size, width, height, preview, app, extra IS NOT NULL
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
                        PreviewText = kind == ClipKind.Image ? "" : _crypto.DecryptString(preview),
                        Thumbnail = kind == ClipKind.Image ? _crypto.Decrypt(preview) : null,
                        SourceApp = r.IsDBNull(9) ? null : _crypto.DecryptString((byte[])r[9]),
                        HasFormatting = r.GetInt64(10) != 0,
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
            using var cmd = Cmd("SELECT kind, payload, width, height, extra FROM clips WHERE id=$id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var kind = (ClipKind)r.GetInt32(0);
            var data = _crypto.Decrypt((byte[])r[1]);
            var (html, rtf) = r.IsDBNull(4) ? (null, null) : ClipContent.ParseFormatting(_crypto.Decrypt((byte[])r[4]));
            return kind switch
            {
                ClipKind.Image => new ClipContent(ClipKind.Image, null, data, null, r.GetInt32(2), r.GetInt32(3)),
                ClipKind.Files => new ClipContent(ClipKind.Files, Encoding.UTF8.GetString(data), null, null),
                _ => ClipContent.FromText(Encoding.UTF8.GetString(data), html: html, rtf: rtf),
            };
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
