using System.Text.Json;

namespace Typlo.Core.Languages;

public sealed record PersonalWord(string Word, string Language);
public sealed record PersonalWordQuery(string Prefix = "", string? Language = null, int Page = 0, int PageSize = 50, bool Exclusions = false);
public sealed record PersonalWordPage(PersonalWord[] Items, int Total, int Page, int PageSize);
public sealed record PersonalImportResult(int Added, int Skipped);

public sealed partial class PersonalLexicon
{
    public const int MaxImportWords = 100_000;
    public const int MaxImportBytes = 64 * 1024 * 1024;

    private static PersonalWord Validate(PersonalWord entry)
    {
        if (entry is null || entry.Word is null || entry.Word.Length > 128 || entry.Language is null) throw new ArgumentException("Invalid word or language.");
        var word = LanguageLexicon.Normalize(entry.Word.Trim());
        if (!IsWord(word) || !LanguageCodes.Contains(entry.Language)) throw new ArgumentException("Invalid word or language.");
        return new(word, entry.Language);
    }

    public PersonalWordPage Query(PersonalWordQuery query)
    {
        if (query.Prefix.Length > 128 || query.Page < 0 || query.Page > 1_000_000 || query.PageSize is < 1 or > 100 ||
            (query.Language is not null && !LanguageCodes.Contains(query.Language))) throw new ArgumentException("Invalid dictionary query.");
        var prefix = LanguageLexicon.Normalize(query.Prefix.Trim());
        // An indexed ordinal prefix range works for non-ASCII words too, unlike SQLite NOCASE.
        var upper = prefix.Length == 0 ? "" : prefix[..^1] + (char)(prefix[^1] + 1);
        var table = query.Exclusions ? "excluded_words" : "words";
        var where = (query.Exclusions || query.Language is null ? "" : "language=$language AND ") + (prefix.Length == 0 ? "1=1" : "word >= $prefix AND word < $upper");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is null && !File.Exists(path)) return new([], 0, 0, query.PageSize);
            using var snapshot = Open().BeginTransaction(deferred: true);
            using var command = Open().CreateCommand();
            command.Transaction = snapshot;
            command.Parameters.AddWithValue("$language", query.Language ?? "");
            command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$upper", upper);
            command.CommandText = "SELECT count(*) FROM " + table + " WHERE " + where;
            var total = Convert.ToInt32(command.ExecuteScalar());
            var page = Math.Min(query.Page, Math.Max(0, (total - 1) / query.PageSize));
            command.CommandText = (query.Exclusions ? "SELECT word,'' FROM " : "SELECT word,language FROM ") + table + " WHERE " + where + (query.Exclusions ? " ORDER BY word" : " ORDER BY word,language") + " LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", query.PageSize); command.Parameters.AddWithValue("$offset", page * query.PageSize);
            using var rows = command.ExecuteReader();
            var result = new List<PersonalWord>(query.PageSize);
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1)));
            return new(result.ToArray(), total, page, query.PageSize);
        }
    }

    public void Edit(PersonalWord original, PersonalWord replacement, bool exclusions = false)
    {
        original = exclusions ? ValidateExclusion(original) : Validate(original); replacement = exclusions ? ValidateExclusion(replacement) : Validate(replacement);
        lock (_gate)
        {
            using var transaction = Open().BeginTransaction();
            using var command = _connection!.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$word", original.Word); command.Parameters.AddWithValue("$language", original.Language);
            command.CommandText = exclusions ? "DELETE FROM excluded_words WHERE word=$word" : "DELETE FROM words WHERE word=$word AND language=$language";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The entry changed. Refresh the dictionary.");
            command.Parameters[0].Value = replacement.Word; command.Parameters[1].Value = replacement.Language;
            command.CommandText = exclusions ? "INSERT INTO excluded_words VALUES ($word)" : "INSERT INTO words VALUES ($word,$language)";
            command.ExecuteNonQuery(); // A duplicate rolls the deletion back; never discard another entry.
            transaction.Commit();
        }
    }

    public PersonalImportResult AddMany(IReadOnlyList<PersonalWord> entries, bool exclusions = false)
    {
        if (entries.Count > MaxImportWords) throw new InvalidDataException("At most 100000 words can be imported at once.");
        var validated = entries.Select(entry => exclusions ? ValidateExclusion(entry) : Validate(entry)).ToArray(); // Complete validation precedes any mutation.
        lock (_gate)
        {
            using var transaction = Open().BeginTransaction();
            using var command = _connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = exclusions ? "INSERT OR IGNORE INTO excluded_words VALUES ($word)" : "INSERT OR IGNORE INTO words VALUES ($word,$language)";
            command.Parameters.AddWithValue("$word", ""); command.Parameters.AddWithValue("$language", ""); command.Prepare();
            var added = 0;
            foreach (var entry in validated)
            {
                command.Parameters[0].Value = entry.Word; command.Parameters[1].Value = entry.Language;
                added += command.ExecuteNonQuery();
            }
            transaction.Commit(); return new(added, entries.Count - added);
        }
    }

    public PersonalImportResult Import(string file, bool exclusions = false)
    {
        using var stream = File.OpenRead(file);
        return Import(stream, exclusions);
    }

    public PersonalImportResult Import(Stream stream, bool exclusions = false)
    {
        if (stream.CanSeek && stream.Length > MaxImportBytes) throw new InvalidDataException("Dictionary import is limited to 64 MiB.");
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); Open(); }
        // Slow file I/O must not hold the reader's gate or stall automatic/manual text tools.
        using var worker = new PersonalLexicon(path);
        return worker.ImportCore(stream, exclusions);
    }

    private PersonalImportResult ImportCore(Stream stream, bool exclusions)
    {
        lock (_gate)
        {
            using (var setup = Open().CreateCommand())
            { setup.CommandText = "PRAGMA temp_store=FILE; PRAGMA temp.cache_size=-32;"; setup.ExecuteNonQuery(); }
            using var transaction = Open().BeginTransaction(deferred: true);
            using var command = _connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "CREATE TEMP TABLE word_import (word TEXT NOT NULL, language TEXT NOT NULL, PRIMARY KEY(word,language)) WITHOUT ROWID;";
            command.ExecuteNonQuery();
            try
            {
                command.CommandText = "INSERT OR IGNORE INTO temp.word_import VALUES ($word,$language)";
                command.Parameters.AddWithValue("$word", ""); command.Parameters.AddWithValue("$language", ""); command.Prepare();
                var count = ReadImport(stream, entry =>
                {
                    var valid = exclusions ? ValidateExclusion(entry) : Validate(entry); command.Parameters[0].Value = valid.Word; command.Parameters[1].Value = valid.Language; command.ExecuteNonQuery();
                }, exclusions);
                command.Parameters.Clear();
                command.CommandText = exclusions ? "INSERT OR IGNORE INTO excluded_words SELECT word FROM temp.word_import" : "INSERT OR IGNORE INTO words SELECT word,language FROM temp.word_import";
                var added = command.ExecuteNonQuery();
                command.CommandText = "DROP TABLE temp.word_import"; command.ExecuteNonQuery();
                transaction.Commit(); return new(added, count - added);
            }
            catch { transaction.Rollback(); throw; }
        }
    }

    private static int ReadImport(Stream stream, Action<PersonalWord> accept, bool exclusions)
    {
        // A small streaming parser plus a disk-backed staging table keeps large imports bounded.
        var buffer = new byte[4096];
        var retained = stream.ReadAtLeast(buffer.AsSpan(0, 3), 3, throwOnEndOfStream: false);
        long bytes = retained;
        if (retained == 3 && buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })) retained = 0;
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 4 });
        var format = false; var version = false; var words = false; var inWords = false; var inEntry = false; var complete = false;
        string? property = null, word = null, language = null; var count = 0;
        while (true)
        {
            var read = stream.Read(buffer.AsSpan(retained)); bytes += read;
            if (bytes > MaxImportBytes) throw new InvalidDataException("Dictionary import is limited to 64 MiB.");
            var final = read == 0;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, retained + read), final, state);
            while (reader.Read())
            {
                var depth = reader.CurrentDepth; var token = reader.TokenType;
                if (token == JsonTokenType.StartObject && depth == 0 && !complete) continue;
                if (token == JsonTokenType.PropertyName && depth == 1 && !inWords) { property = reader.GetString(); continue; }
                if (token == JsonTokenType.String && depth == 1 && property == "format" && !format)
                { if (reader.GetString() != (exclusions ? "typlo-auto-correction-exclusions" : "typlo-personal-dictionary")) throw InvalidImport(); format = true; property = null; continue; }
                if (token == JsonTokenType.Number && depth == 1 && property == "version" && !version)
                { if (reader.GetInt32() != 1) throw InvalidImport(); version = true; property = null; continue; }
                if (token == JsonTokenType.StartArray && depth == 1 && property == "words" && !words)
                { words = inWords = true; property = null; continue; }
                if (exclusions && token == JsonTokenType.String && depth == 2 && inWords)
                {
                    if (++count > MaxImportWords) throw new InvalidDataException("At most 100000 words can be imported at once.");
                    accept(new(reader.GetString()!, "")); continue;
                }
                if (!exclusions && token == JsonTokenType.StartObject && depth == 2 && inWords && !inEntry)
                { inEntry = true; word = language = null; continue; }
                if (token == JsonTokenType.PropertyName && depth == 3 && inEntry) { property = reader.GetString(); continue; }
                if (token == JsonTokenType.String && depth == 3 && inEntry)
                {
                    if (property == "word" && word is null) word = reader.GetString();
                    else if (property == "language" && language is null) language = reader.GetString();
                    else throw InvalidImport();
                    property = null; continue;
                }
                if (token == JsonTokenType.EndObject && depth == 2 && inEntry && word is not null && language is not null)
                {
                    if (++count > MaxImportWords) throw new InvalidDataException("At most 100000 words can be imported at once.");
                    accept(new(word, language)); inEntry = false; continue;
                }
                if (token == JsonTokenType.EndArray && depth == 1 && inWords && !inEntry) { inWords = false; continue; }
                if (token == JsonTokenType.EndObject && depth == 0 && format && version && words && !inWords) { complete = true; continue; }
                throw InvalidImport();
            }
            var remaining = retained + read - (int)reader.BytesConsumed;
            buffer.AsSpan((int)reader.BytesConsumed, remaining).CopyTo(buffer);
            retained = remaining; state = reader.CurrentState;
            if (final) { if (!complete || retained != 0) throw InvalidImport(); return count; }
            if (retained == buffer.Length) throw new InvalidDataException("A dictionary field is too long.");
        }
        static InvalidDataException InvalidImport() => new("Not a supported Typlo personal dictionary.");
    }

    public void Export(string file, bool exclusions = false)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        using var worker = new PersonalLexicon(path);
        worker.ExportCore(file, exclusions);
    }

    private void ExportCore(string file, bool exclusions)
    {
        var target = Path.GetFullPath(file);
        if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || string.Equals(target, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .json file for the export.");
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                writer.WriteStartObject(); writer.WriteString("format", exclusions ? "typlo-auto-correction-exclusions" : "typlo-personal-dictionary"); writer.WriteNumber("version", 1); writer.WriteStartArray("words");
                if (_connection is not null || File.Exists(path))
                {
                    using var command = Open().CreateCommand(); command.CommandText = exclusions ? "SELECT word FROM excluded_words ORDER BY word" : "SELECT word,language FROM words ORDER BY word,language";
                    using var rows = command.ExecuteReader();
                    while (rows.Read())
                    {
                        if (exclusions) writer.WriteStringValue(rows.GetString(0));
                        else { writer.WriteStartObject(); writer.WriteString("word", rows.GetString(0)); writer.WriteString("language", rows.GetString(1)); writer.WriteEndObject(); }
                        if (writer.BytesPending >= 16 * 1024) writer.Flush(); // Bounded buffering, independent of dictionary size.
                    }
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
