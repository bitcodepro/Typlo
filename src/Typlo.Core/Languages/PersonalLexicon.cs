using Microsoft.Data.Sqlite;

namespace Typlo.Core.Languages;

/// <summary>User-owned exact forms, indexed on disk; never load the full vocabulary into RAM.</summary>
public sealed partial class PersonalLexicon(string path) : IDisposable
{
    public static IReadOnlyList<string> LanguageCodes { get; } = Array.AsReadOnly(new[] { "en", "ru", "uk", "fr", "es", "it", "de", "pt" });
    private readonly Lock _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public static bool IsWord(string? text) => text is { Length: >= 2 and <= 128 } && char.IsLetter(text[0]) && char.IsLetter(text[^1]) &&
        text.Select((c, i) => char.IsLetter(c) || (c is '\'' or '-' && i > 0 && i < text.Length - 1 && char.IsLetter(text[i - 1]) && char.IsLetter(text[i + 1]))).All(x => x);

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null) return _connection;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-32; PRAGMA mmap_size=0; CREATE TABLE IF NOT EXISTS words (word TEXT NOT NULL, language TEXT NOT NULL, PRIMARY KEY(word,language)) WITHOUT ROWID; CREATE INDEX IF NOT EXISTS words_language ON words(language,word);";
            command.ExecuteNonQuery();
            return _connection = connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public byte Lookup(string text)
    {
        var word = LanguageLexicon.Normalize(text);
        if (!IsWord(word)) return 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is null && !File.Exists(path)) return 0;
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT language FROM words WHERE word=$word";
            command.Parameters.AddWithValue("$word", word);
            using var reader = command.ExecuteReader();
            byte mask = 0;
            while (reader.Read())
            {
                var language = reader.GetString(0);
                for (var i = 0; i < LanguageCodes.Count; i++) if (LanguageCodes[i] == language) { mask |= (byte)(1 << i); break; }
            }
            return mask;
        }
    }

    public void Set(string text, string language, bool remove = false)
    {
        var word = LanguageLexicon.Normalize(text.Trim());
        if (!IsWord(word) || !LanguageCodes.Contains(language)) throw new ArgumentException("Choose a language and enter one word (letters, internal apostrophes or hyphens).");
        lock (_gate)
        {
            using var command = Open().CreateCommand();
            command.CommandText = remove ? "DELETE FROM words WHERE word=$word AND language=$language" : "INSERT OR IGNORE INTO words VALUES ($word,$language)";
            command.Parameters.AddWithValue("$word", word);
            command.Parameters.AddWithValue("$language", language);
            command.ExecuteNonQuery();
        }
    }

    public void Close() { lock (_gate) { _connection?.Dispose(); _connection = null; } }
    public void Dispose() { lock (_gate) { _disposed = true; Close(); } }
}
