using System.Text;
using Microsoft.Data.Sqlite;
using Typlo.Core.Languages;

namespace Typlo.Tests;

public sealed class ExcludedWordStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-excluded-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_dir, "personal.sqlite3");

    [Fact]
    public void ExclusionsAreLazyExactNormalizedAndSeparateFromLanguageEvidence()
    {
        using var words = new PersonalLexicon(Database);
        Assert.False(words.IsExcluded("sql")); Assert.Empty(words.Query(new(Exclusions: true)).Items);
        Assert.False(File.Exists(Database));
        words.Set("sql", "en"); words.SetExclusion("SQL"); words.SetExclusion("cafe\u0301");
        words.SetExclusion("C#"); words.SetExclusion("O’Neil");
        Assert.True(words.IsExcluded("sql!")); Assert.True(words.IsExcluded("(SQL)"));
        Assert.False(words.IsExcluded("sqlserver")); Assert.True(words.IsExcluded("café"));
        Assert.True(words.IsExcluded("c#")); Assert.True(words.IsExcluded("o'neil"));
        Assert.Equal(1, words.Lookup("sql")); Assert.Equal(0, words.Lookup("café"));
        Assert.Single(words.Query(new()).Items); Assert.Equal(4, words.Query(new(Exclusions: true)).Total);
        Assert.Throws<ArgumentException>(() => words.SetExclusion("two words"));
        words.SetExclusion("SQL", remove: true); Assert.False(words.IsExcluded("sql")); Assert.Equal(1, words.Lookup("sql"));
    }

    [Fact]
    public void ExistingDictionaryMigratesWithoutLosingEntriesAndAnotherReaderSeesChanges()
    {
        Directory.CreateDirectory(_dir);
        using (var old = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            old.Open(); using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE words(word TEXT NOT NULL,language TEXT NOT NULL,PRIMARY KEY(word,language)) WITHOUT ROWID; INSERT INTO words VALUES('напиши','ru');";
            command.ExecuteNonQuery();
        }
        using var reader = new PersonalLexicon(Database); using var writer = new PersonalLexicon(Database);
        Assert.Equal(2, reader.Lookup("напиши")); writer.SetExclusion("ghb"); Assert.True(reader.IsExcluded("ghb"));
        writer.Edit(new("ghb", ""), new("yfgbib", ""), exclusions: true);
        Assert.False(reader.IsExcluded("ghb")); Assert.True(reader.IsExcluded("yfgbib")); Assert.Equal(2, reader.Lookup("напиши"));
    }

    [Fact]
    public void PrefixPagingAndAtomicEditingDoNotOverwriteExistingExceptions()
    {
        using var words = new PersonalLexicon(Database);
        words.AddMany(Enumerable.Range(0, 160).Select(i => new PersonalWord("Term" + i.ToString("D3"), "")).ToArray(), exclusions: true);
        Assert.Equal(160, words.Query(new(Exclusions: true)).Total);
        Assert.Equal(50, words.Query(new(Page: 1, Exclusions: true)).Items.Length);
        Assert.Equal(10, words.Query(new("TERM01", Exclusions: true)).Total);
        Assert.Throws<SqliteException>(() => words.Edit(new("term001", ""), new("term002", ""), exclusions: true));
        Assert.True(words.IsExcluded("term001")); Assert.True(words.IsExcluded("term002"));
        Assert.Throws<ArgumentException>(() => words.AddMany([new("valid", ""), new("two words", "")], exclusions: true));
        Assert.False(words.IsExcluded("valid"));
    }

    [Fact]
    public void ExclusionsRoundTripSeparatelyAndDamagedOrWrongFormatImportsAreAtomic()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru");
        words.SetExclusion("SQL"); words.SetExclusion("café");
        var file = Path.Combine(_dir, "excluded.json"); words.Export(file, exclusions: true);
        using var imported = new PersonalLexicon(Path.Combine(_dir, "other.sqlite3"));
        Assert.Equal(new(2, 0), imported.Import(file, exclusions: true)); Assert.Equal(new(0, 2), imported.Import(file, exclusions: true));
        Assert.True(imported.IsExcluded("SQL")); Assert.Empty(imported.Query(new()).Items);
        Assert.Throws<InvalidDataException>(() => imported.Import(file));
        using var damaged = new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"typlo-auto-correction-exclusions","version":1,"words":["keepnew","invalid term"]}"""));
        Assert.Throws<ArgumentException>(() => imported.Import(damaged, exclusions: true));
        Assert.False(imported.IsExcluded("keepnew")); Assert.Equal(2, imported.Query(new(Exclusions: true)).Total);
        words.Export(file); Assert.Throws<InvalidDataException>(() => imported.Import(file, exclusions: true));
        Assert.Equal(2, imported.Query(new(Exclusions: true)).Total);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
