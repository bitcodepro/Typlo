namespace Typlo.Core.Languages;

public sealed partial class PersonalLexicon
{
    private static PersonalWord ValidateExclusion(PersonalWord entry)
    {
        if (entry?.Word is null) throw new ArgumentException("Enter a word or term.");
        var word = NormalizeExclusion(entry.Word);
        if (word.Length is < 1 or > 128 || word.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Enter one exact word or term, without spaces.");
        return new(word, "");
    }

    private static string NormalizeExclusion(string text) => LanguageLexicon.Normalize(text.Trim());

    public bool IsExcluded(string text)
    {
        var word = NormalizeExclusion(text);
        if (word.Length is < 1 or > 128) return false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is null && !File.Exists(path)) return false;
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT 1 FROM excluded_words WHERE word=$word OR word=$token LIMIT 1";
            command.Parameters.AddWithValue("$word", word);
            command.Parameters.AddWithValue("$token", word.Trim('.', ',', '!', '?', ';', ':', '"', '(', ')', '[', ']', '{', '}', '\u00ab', '\u00bb', '\u2026'));
            return command.ExecuteScalar() is not null;
        }
    }

    public void SetExclusion(string text, bool remove = false)
    {
        var word = ValidateExclusion(new(text, "")).Word;
        lock (_gate)
        {
            using var command = Open().CreateCommand();
            command.CommandText = remove ? "DELETE FROM excluded_words WHERE word=$word" : "INSERT OR IGNORE INTO excluded_words VALUES ($word)";
            command.Parameters.AddWithValue("$word", word); command.ExecuteNonQuery();
        }
    }
}
