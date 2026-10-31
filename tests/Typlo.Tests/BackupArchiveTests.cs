using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Typlo.Core.Languages;
using Typlo.Core.Settings;
using Typlo.Core.Text;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Storage;

namespace Typlo.Tests;

public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typlo-backup-tests-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "personal.sqlite3");
    private string Archive => Path.Combine(_directory, "backup.typlo");
    [Fact]
    public void AllUserConfigurationAndBothCollectionsRoundTripWithoutHistory()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru"); words.SetExclusion("ghb");
        var settings = new AppSettings { Snippets = [new() { Trigger = ";hi", Text = "hello\n{cursor}" }],
            ApplicationLayouts = [new() { ProcessName = "editor", Layouts = [], AutoCorrection = false, SnippetExpansion = true }] };
        BackupArchive.Write(Archive, settings, words);
        using var stage = BackupArchive.Read(Archive, _directory);
        Assert.Equal((1, 1), (stage.Info.Words, stage.Info.Exclusions)); Assert.Equal(1, stage.Info.Snippets); Assert.Equal(1, stage.Info.Profiles);
        Assert.False(stage.Settings.ApplicationLayouts[0].AutoCorrection); Assert.Equal(settings.Snippets[0].Text, stage.Settings.Snippets[0].Text);
        using var zip = ZipFile.OpenRead(Archive); Assert.Equal(new[] { "format.txt", "personal.sqlite3", "settings.json" }, zip.Entries.Select(e => e.FullName).Order().ToArray());
        words.Set("другое", "ru"); words.ReplaceSnapshot(stage.Database, () => { });
        Assert.Equal(0, words.Lookup("другое")); Assert.Equal(2, words.Lookup("напиши")); Assert.True(words.IsExcluded("ghb"));
        words.Set("новое", "ru"); words.Set("нове", "uk");
        Assert.Equal(2, words.Lookup("новое")); Assert.Equal(4, words.Lookup("нове"));
    }
    [Fact]
    public void InvalidArchivesAndWrongFormatsCannotEscapeStagingOrTouchLiveData()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru");
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create)) { var entry = zip.CreateEntry("../escape.txt"); using var writer = new StreamWriter(entry.Open()); writer.Write("unexpected"); }
        Assert.Throws<InvalidDataException>(() => BackupArchive.Read(Archive, _directory)); Assert.False(File.Exists(Path.Combine(_directory, "escape.txt")));
        Assert.Equal(2, words.Lookup("напиши")); Assert.Empty(Directory.GetDirectories(_directory, ".backup-*"));
    }
    [Fact]
    public void AFailedSettingsApplicationRollsBackTheDictionaryAsWell()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru"); BackupArchive.Write(Archive, new(), words);
        using var stage = BackupArchive.Read(Archive, _directory); words.SetExclusion("keep"); words.Set("написал", "ru");
        Assert.Throws<IOException>(() => words.ReplaceSnapshot(stage.Database, () => throw new IOException("fixture settings failure")));
        Assert.True(words.IsExcluded("keep")); Assert.Equal(2, words.Lookup("написал")); Assert.Equal(2, words.Lookup("напиши"));
    }
    [Fact]
    public void RecoveryRestoresBothFilesAfterAnInterruptedRestoreAndRecognizesACommittedOne()
    {
        using var words = new PersonalLexicon(Database); words.Set("напиши", "ru");
        var journal = BackupArchive.RecoveryDirectory(_directory); Directory.CreateDirectory(journal);
        words.SaveSnapshot(Path.Combine(journal, "personal.sqlite3"));
        var settings = new AppSettings { AutoCorrectEnabled = false }; new SettingsStore(Path.Combine(journal, "settings.json")).Export(settings, Path.Combine(journal, "settings.json"));
        BackupArchive.Recover(_directory);
        using var recovered = new PersonalLexicon(Path.Combine(_directory, "personal-words.sqlite3")); Assert.Equal(2, recovered.Lookup("напиши"));
        Assert.False(new SettingsStore(Path.Combine(_directory, "settings.json")).Load().AutoCorrectEnabled);
        Directory.CreateDirectory(journal); File.WriteAllText(Path.Combine(journal, "committed"), "ok");
        BackupArchive.Recover(_directory); Assert.False(Directory.Exists(journal));
    }
    [Fact]
    public void SnapshotValidationRejectsTriggersAndMissingPrimaryKeys()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE words(word TEXT NOT NULL,language TEXT NOT NULL); CREATE TABLE excluded_words(word TEXT NOT NULL PRIMARY KEY);";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => PersonalLexicon.ValidateSnapshot(Database));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

public sealed partial class AppControllerTests
{
    [Fact]
    public async Task SuccessfulRollbackRetiresJournalAndPreservesLaterEditsAcrossRestart()
    {
        var file = Path.Combine(_dir, "rollback.typlo");
        await _controller.SetPersonalWordAsync("напиши", "ru");
        var original = _controller.Settings.Clone(); original.SnippetsEnabled = false; _controller.ApplySettings(original);
        await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file);
        var current = _controller.Settings.Clone(); current.SnippetsEnabled = true; _controller.ApplySettings(current);
        await _controller.SetExcludedWordAsync("sql"); await _controller.SetPersonalWordAsync("написал", "ru");
        _controller.SettingsChanged += () => { if (!_controller.Settings.SnippetsEnabled) throw new InvalidOperationException("fixture applying settings failed"); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.RestoreBackupAsync(file, info.Hash));
        Assert.True(_controller.Settings.SnippetsEnabled); Assert.Equal(2, (await _controller.QueryPersonalWordsAsync(new())).Total);
        Assert.Single((await _controller.QueryPersonalWordsAsync(new(Exclusions: true))).Items);
        Assert.False(Directory.Exists(BackupArchive.RecoveryDirectory(_dir)));
        var later = _controller.Settings.Clone(); later.MaxItems = 123; _controller.ApplySettings(later);
        await _controller.SetPersonalWordAsync("добавлено", "ru"); await _controller.SetExcludedWordAsync("api");
        _controller.Dispose();
        using var restarted = new AppController(new FakePlatform(), new AppPaths(_dir), new PlainKeyProtector(), diagnostic: true);
        Assert.Equal(123, restarted.Settings.MaxItems); Assert.True(restarted.Settings.SnippetsEnabled);
        Assert.Equal(3, (await restarted.QueryPersonalWordsAsync(new())).Total);
        Assert.Equal(2, (await restarted.QueryPersonalWordsAsync(new(Exclusions: true))).Total);
    }

    [Fact]
    public async Task IncompleteRollbackBlocksAllConfigurationWritesUntilRestartRecovery()
    {
        var file = Path.Combine(_dir, "incomplete.typlo");
        await _controller.SetPersonalWordAsync("напиши", "ru");
        var settings = _controller.Settings.Clone(); settings.SnippetsEnabled = false; _controller.ApplySettings(settings);
        await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file);
        settings = _controller.Settings.Clone(); settings.SnippetsEnabled = true; _controller.ApplySettings(settings);
        Task? queuedWrite = null;
        _controller.SettingsChanged += () =>
        {
            queuedWrite ??= _controller.SetPersonalWordAsync("очередь", "ru");
            throw new IOException("fixture callback fails during apply and rollback");
        };
        await Assert.ThrowsAsync<AggregateException>(() => _controller.RestoreBackupAsync(file, info.Hash));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queuedWrite!);
        Assert.True(Directory.Exists(BackupArchive.RecoveryDirectory(_dir)));
        Assert.Throws<InvalidOperationException>(() => _controller.ApplySettings(_controller.Settings.Clone()));
        Assert.Throws<InvalidOperationException>(() => _controller.RefreshKeyboardLayouts());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.SetPersonalWordAsync("добавлено", "ru"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.SetExcludedWordAsync("api"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.AddPersonalWordsAsync([new("добавлено", "ru")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.EditPersonalWordAsync(new("напиши", "ru"), new("добавлено", "ru")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ImportPersonalWordsAsync(Path.Combine(_dir, "unused.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ReplaceUpdateFilesAsync(() => throw new Exception("must not run")));
        _controller.Dispose();
        using var restarted = new AppController(new FakePlatform(), new AppPaths(_dir), new PlainKeyProtector(), diagnostic: true);
        Assert.True(restarted.Settings.SnippetsEnabled); Assert.Single((await restarted.QueryPersonalWordsAsync(new())).Items);
        await restarted.SetPersonalWordAsync("добавлено", "ru"); Assert.Equal(2, (await restarted.QueryPersonalWordsAsync(new())).Total);
        Assert.False(Directory.Exists(BackupArchive.RecoveryDirectory(_dir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRestoreCannotEvictHistoryBeforeCommit(bool failCommitMarker)
    {
        var file = Path.Combine(_dir, "retention.typlo");
        var settings = _controller.Settings.Clone(); settings.SnippetsEnabled = false; settings.MaxItems = 10; _controller.ApplySettings(settings);
        await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file);
        settings = _controller.Settings.Clone(); settings.SnippetsEnabled = true; settings.MaxItems = 500; _controller.ApplySettings(settings);
        for (var i = 0; i < 100; i++) _controller.Store.Add(ClipContent.FromText("history " + i));
        var channel = "Typlo.Test." + Guid.NewGuid().ToString("N"); using var server = RemoteServer(channel);
        _controller.SettingsChanged += () =>
        {
            if (_controller.Settings.SnippetsEnabled) return;
            // A UI process can refresh while staged settings are on disk; it must not prune history.
            using var ui = AppController.CreateRemote(new FakePlatform(), new AppPaths(_dir), new PlainKeyProtector(), new RuntimeClient(channel));
            Assert.Equal(10, ui.Settings.MaxItems);
            Assert.Equal(100, _controller.Store.Stats().Count);
            _controller.Store.Add(ClipContent.FromText("copy during restore"));
            Assert.Equal(101, _controller.Store.Stats().Count);
            if (failCommitMarker) Directory.CreateDirectory(Path.Combine(BackupArchive.RecoveryDirectory(_dir), "committed"));
            else throw new IOException("fixture post-application failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _controller.RestoreBackupAsync(file, info.Hash));
        Assert.Equal(500, _controller.Settings.MaxItems); Assert.Equal(101, _controller.Store.Stats().Count);
        _controller.Dispose();
        using var restarted = new AppController(new FakePlatform(), new AppPaths(_dir), new PlainKeyProtector(), diagnostic: true);
        Assert.Equal(500, restarted.Settings.MaxItems); Assert.Equal(101, restarted.Store.Stats().Count);
    }

    [Fact]
    public async Task SuccessfulRestoreEnforcesHistoryLimitsOnlyAfterConfigurationCommit()
    {
        var file = Path.Combine(_dir, "limits.typlo");
        var settings = _controller.Settings.Clone(); settings.MaxItems = 10; _controller.ApplySettings(settings);
        await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file);
        settings = _controller.Settings.Clone(); settings.MaxItems = 500; _controller.ApplySettings(settings);
        for (var i = 0; i < 100; i++) _controller.Store.Add(ClipContent.FromText("history " + i));
        _controller.SettingsChanged += () => { if (_controller.Settings.MaxItems == 10) Assert.Equal(100, _controller.Store.Stats().Count); };
        await _controller.RestoreBackupAsync(file, info.Hash);
        Assert.Equal(10, _controller.Store.Stats().Count);
    }

    [Theory]
    [InlineData("CHECK(language='en')")]
    [InlineData("COLLATE NOCASE")]
    [InlineData("UNIQUE INDEX")]
    public async Task ArchiveWithUnexpectedSchemaCannotChangeLiveSettingsOrDictionary(string alteration)
    {
        var file = Path.Combine(_dir, "schema.typlo"); var database = Path.Combine(_dir, "unexpected.sqlite3");
        await _controller.SetPersonalWordAsync("напиши", "ru"); await _controller.SetExcludedWordAsync("api");
        var settings = _controller.Settings.Clone(); settings.MaxItems = 111; _controller.ApplySettings(settings);
        await _controller.ExportBackupAsync(file);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE words (word TEXT " + (alteration == "COLLATE NOCASE" ? "COLLATE NOCASE " : "") +
                "NOT NULL, language TEXT NOT NULL, PRIMARY KEY(word,language)" + (alteration.StartsWith("CHECK") ? ", " + alteration : "") +
                ") WITHOUT ROWID; CREATE INDEX words_language ON words(language,word); CREATE TABLE excluded_words (word TEXT NOT NULL PRIMARY KEY) WITHOUT ROWID;" +
                (alteration == "UNIQUE INDEX" ? "CREATE UNIQUE INDEX unexpected ON words(language);" : "");
            command.ExecuteNonQuery();
        }
        using (var archive = ZipFile.Open(file, ZipArchiveMode.Update))
        { archive.GetEntry("personal.sqlite3")!.Delete(); archive.CreateEntryFromFile(database, "personal.sqlite3"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => _controller.PreviewBackupAsync(file));
        await Assert.ThrowsAsync<InvalidDataException>(() => _controller.RestoreBackupAsync(file, "unused"));
        Assert.Single((await _controller.QueryPersonalWordsAsync(new())).Items);
        Assert.Single((await _controller.QueryPersonalWordsAsync(new(Exclusions: true))).Items);
        Assert.Equal(111, _controller.Settings.MaxItems);
        Assert.False(Directory.Exists(BackupArchive.RecoveryDirectory(_dir)));
    }

    [Fact]
    public async Task FullBackupRestoreReplacesSettingsAndWordsAndChecksThePreviewHash()
    {
        var file = Path.Combine(_dir, "backup.typlo"); await _controller.SetPersonalWordAsync("напиши", "ru"); await _controller.SetExcludedWordAsync("sql");
        var settings = _controller.Settings.Clone(); settings.Snippets = [new() { Trigger = ";hi", Text = "hello" }]; settings.SnippetsEnabled = false; _controller.ApplySettings(settings);
        await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file); Assert.Equal(1, info.Words); Assert.Equal(1, info.Exclusions);
        settings = _controller.Settings.Clone(); settings.Snippets = []; settings.SnippetsEnabled = true; _controller.ApplySettings(settings);
        await _controller.SetPersonalWordAsync("написал", "ru");
        await Assert.ThrowsAsync<InvalidDataException>(() => _controller.RestoreBackupAsync(file, "wrong hash"));
        Assert.True(_controller.Settings.SnippetsEnabled); Assert.Equal(2, (await _controller.QueryPersonalWordsAsync(new())).Total);
        await _controller.RestoreBackupAsync(file, info.Hash);
        Assert.False(_controller.Settings.SnippetsEnabled); Assert.Single(_controller.Settings.Snippets); Assert.Single((await _controller.QueryPersonalWordsAsync(new())).Items);
        Assert.Single((await _controller.QueryPersonalWordsAsync(new(Exclusions: true))).Items); Assert.False(Directory.Exists(BackupArchive.RecoveryDirectory(_dir)));
    }
}
