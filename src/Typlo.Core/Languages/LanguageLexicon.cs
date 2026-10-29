using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Typlo.Core.Languages;

public readonly record struct WordEvidence(byte[] CorpusWeights, byte SupplementMask)
{
    public bool Includes(int language) => CorpusWeights[language] > 0 || (SupplementMask & (1 << language)) != 0;
}

/// <summary>Lazy read-only vocabulary with a bounded SQLite page cache; no vocabulary preload.</summary>
public sealed class LanguageLexicon(string path) : IDisposable
{
    private SqliteConnection? _connection;
    private SqliteCommand? _lookup;
    private string[] _languages = [];
    private readonly Lock _gate = new();
    private bool _disposed;
    public bool IsOpen => _connection is not null;
    public IReadOnlyList<string> Languages { get { lock (_gate) { Open(); return _languages; } } }

    private void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null) return;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            // The versioned pack must never be edited in place while open. Immutable bypasses
            // per-query filesystem change checks/locks; updates replace it after readers close.
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-128; PRAGMA mmap_size=0; PRAGMA query_only=ON;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA application_id";
            if (Convert.ToInt32(command.ExecuteScalar()) != 0x54594C44) throw new InvalidDataException("Not a Typlo vocabulary");
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) != 2) throw new InvalidDataException("Unsupported vocabulary version");
            command.CommandText = "SELECT value FROM metadata WHERE key='languages'";
            using var languages = JsonDocument.Parse((string?)command.ExecuteScalar() ?? throw new InvalidDataException("Missing languages"));
            _languages = languages.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (!_languages.SequenceEqual(new[] { "en", "ru", "uk", "fr", "es", "it", "de", "pt" }))
                throw new InvalidDataException("Invalid languages");
            _lookup = connection.CreateCommand();
            _lookup.CommandText = "SELECT weights,supplement_mask FROM words WHERE word=$word";
            _lookup.Parameters.Add("$word", SqliteType.Text);
            _lookup.Prepare();
            _connection = connection;
        }
        catch { _lookup?.Dispose(); _lookup = null; connection.Dispose(); throw; }
    }

    public WordEvidence? Lookup(string token)
    {
        if (token.Length is 0 or > 128) return null;
        string word;
        try { word = Normalize(token); }
        catch (ArgumentException) { return null; }
        lock (_gate)
        {
            Open();
            _lookup!.Parameters[0].Value = word;
            using var row = _lookup.ExecuteReader();
            if (!row.Read()) return null;
            var weights = (byte[])row[0];
            var mask = row.GetInt32(1);
            if (weights.Length != 8 || (mask & ~6) != 0 || (mask == 0 && weights.All(x => x == 0)))
                throw new InvalidDataException("Malformed vocabulary evidence");
            return new(weights, (byte)mask);
        }
    }

    public static string Normalize(string token) => token.Replace('’', '\'').Replace('ʼ', '\'').Replace('‘', '\'')
        .Replace('‐', '-').Replace('‑', '-').ToLowerInvariant().Normalize(NormalizationForm.FormC);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _lookup?.Dispose(); _connection?.Dispose(); _connection = null; }
    }
}

