using System.Text.Json;
using Microsoft.Data.Sqlite;
using Typlo.Core.Languages;

namespace Typlo.Tests;

public sealed class DictionaryManagementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-manage-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_dir, "personal.sqlite3");

    [Fact]
    public void PagingAndUnicodePrefixFilteringAreStableAndBounded()
    {
        using var words = new PersonalLexicon(Database);
        Assert.Empty(words.Query(new()).Items); Assert.False(File.Exists(Database));
        words.AddMany(Enumerable.Range(0, 230).Select(i => new PersonalWord("слово" + (char)('а' + i / 26) + (char)('а' + i % 26), "ru")).ToArray());
        words.Set("напиши", "ru"); words.Set("напиши", "uk"); words.Set("написал", "ru"); words.Set("café", "fr");
        var first = words.Query(new()); var second = words.Query(new(Page: 1));
        Assert.Equal(234, first.Total); Assert.Equal(50, first.Items.Length); Assert.Empty(first.Items.Intersect(second.Items));
        Assert.Equal(2, words.Query(new("НАПИ", "ru")).Total);
        Assert.Single(words.Query(new("напиши", "uk")).Items);
        Assert.Single(words.Query(new("cafe\u0301", "fr")).Items);
        var last = words.Query(new(Page: 999)); Assert.Equal(4, last.Page); Assert.Equal(34, last.Items.Length);
        Assert.Throws<ArgumentException>(() => words.Query(new(PageSize: 1000)));
    }

    [Fact]
    public void EditingIsAtomicAndDoesNotDiscardDuplicatesOrOtherLanguages()
    {
        using var words = new PersonalLexicon(Database);
        words.Set("напиши", "ru"); words.Set("напиши", "uk"); words.Set("написал", "ru");
        Assert.Throws<SqliteException>(() => words.Edit(new("напиши", "ru"), new("написал", "ru")));
        Assert.Equal(3, words.Query(new()).Total); Assert.Equal(6, words.Lookup("напиши"));
        words.Edit(new("напиши", "ru"), new("Написать", "ru"));
        Assert.Equal(2, words.Lookup("написать")); Assert.Equal(4, words.Lookup("напиши"));
        Assert.Throws<InvalidOperationException>(() => words.Edit(new("несуществующее", "ru"), new("другое", "ru")));
        Assert.Equal(3, words.Query(new()).Total);
    }

    [Fact]
    public void ImportValidatesEveryEntryBeforeMutationAndExportRoundTrips()
    {
        Directory.CreateDirectory(_dir); using var words = new PersonalLexicon(Database);
        words.Set("напиши", "ru");
        Assert.Throws<ArgumentException>(() => words.AddMany([new("писать", "ru"), new("two words", "en")]));
        Assert.Equal(1, words.Query(new()).Total);
        var result = words.AddMany([new("Напиши", "ru"), new("напиши", "uk"), new("писать", "ru"), new("писать", "ru")]);
        Assert.Equal(new(2, 2), result);
        var export = Path.Combine(_dir, "export.json"); words.Export(export);
        using var other = new PersonalLexicon(Path.Combine(_dir, "other.sqlite3"));
        Assert.Equal(new(3, 0), other.Import(export)); Assert.Equal(new(0, 3), other.Import(export));
        Assert.Equal(words.Query(new()).Items, other.Query(new()).Items);
        var invalid = Path.Combine(_dir, "invalid.json");
        File.WriteAllText(invalid, """{"format":"typlo-personal-dictionary","version":1,"words":[{"word":"commit","language":"en"},{"word":"invalid word","language":"en"}]}""");
        Assert.Throws<ArgumentException>(() => other.Import(invalid)); Assert.Equal(3, other.Query(new()).Total);
        Assert.Throws<ArgumentException>(() => words.Export(Database));
        Assert.Equal(3, words.Query(new()).Total);
    }

    [Fact]
    public void ImportRejectsOversizedOrUnsupportedFilesWithoutChangingTheDictionary()
    {
        Directory.CreateDirectory(_dir); using var words = new PersonalLexicon(Database);
        var file = Path.Combine(_dir, "input.json");
        File.WriteAllText(file, """{"format":"other","version":1,"words":[]}""");
        Assert.Throws<InvalidDataException>(() => words.Import(file));
        using (var oversized = new FileStream(file, FileMode.Create)) oversized.SetLength(PersonalLexicon.MaxImportBytes + 1);
        Assert.Throws<InvalidDataException>(() => words.Import(file));
        Assert.Equal(0, words.Query(new()).Total);
        Assert.Throws<InvalidDataException>(() => words.AddMany(new PersonalWord[PersonalLexicon.MaxImportWords + 1]));
    }

    [Fact]
    public void LiveReaderSeesEditsFromAnotherManagementConnection()
    {
        using var reader = new PersonalLexicon(Database); using var editor = new PersonalLexicon(Database);
        editor.Set("ревьюкать", "ru"); Assert.Equal(2, reader.Lookup("ревьюкать"));
        editor.Edit(new("ревьюкать", "ru"), new("ревьюкаешь", "ru"));
        Assert.Equal(0, reader.Lookup("ревьюкать")); Assert.Equal(2, reader.Lookup("ревьюкаешь"));
    }

    [Fact]
    public void LargeUnicodeExportImportsAcrossBufferBoundariesAndRejectsLateDamageAtomically()
    {
        using var original = new PersonalLexicon(Database);
        var entries = Enumerable.Range(0, 12_000).Select(i => new PersonalWord("слово" + (char)('а' + i / 26 / 26) + (char)('а' + i / 26 % 26) + (char)('а' + i % 26), "ru")).ToArray();
        original.AddMany(entries);
        var file = Path.Combine(_dir, "large.json"); original.Export(file);
        using var destination = new PersonalLexicon(Path.Combine(_dir, "destination.sqlite3"));
        Assert.Equal(new(12_000, 0), destination.Import(file));
        Assert.Equal(12_000, destination.Query(new()).Total);
        var content = File.ReadAllText(file);
        File.WriteAllText(file, content[..^2]);
        using var empty = new PersonalLexicon(Path.Combine(_dir, "empty.sqlite3"));
        Assert.ThrowsAny<JsonException>(() => empty.Import(file)); Assert.Equal(0, empty.Query(new()).Total);
        File.WriteAllText(file, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(new(12_000, 0), empty.Import(file)); // Includes rollback cleanup and BOM handling.
    }

    [Fact]
    public async Task SlowImportDoesNotHoldTheLiveLookupGateOrTheMainWriterTransaction()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new PausedImportStream(System.Text.Encoding.UTF8.GetBytes("""{"format":"typlo-personal-dictionary","version":1,"words":[{"word":"писать","language":"ru"}]}"""), entered, release);
        var importing = Task.Run(() => words.Import(input));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, await Task.Run(() => words.Lookup("напиши")).WaitAsync(TimeSpan.FromSeconds(3)));
            await Task.Run(() => words.Set("написал", "ru")).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(importing.IsCompleted);
            release.TrySetResult(); Assert.Equal(new(1, 0), await importing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, words.Query(new()).Total);
        }
        finally { release.TrySetResult(); await importing; }
    }

    [Fact]
    public void FailedExportPreservesTheDestinationAndRemovesItsTemporaryFile()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru");
        var destination = Path.Combine(_dir, "existing.json"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "existing data");
        var error = Record.Exception(() => words.Export(destination));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("existing data", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*")); Assert.Equal(2, words.Lookup("напиши"));
    }

    private sealed class PausedImportStream(byte[] data, TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream(data)
    {
        private int _paused;
        public override int Read(Span<byte> buffer)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            { entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            return base.Read(buffer);
        }
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
