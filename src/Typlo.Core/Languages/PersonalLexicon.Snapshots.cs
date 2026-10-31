using Microsoft.Data.Sqlite;

namespace Typlo.Core.Languages;

public sealed partial class PersonalLexicon
{
    private readonly ReaderWriterLockSlim _maintenance = new();
    public void SaveSnapshot(string destination)
    {
        _maintenance.EnterReadLock();
        try
        {
            lock (_gate)
            {
                SaveSnapshotCore(destination);
            }
        }
        finally { _maintenance.ExitReadLock(); }
    }

    private void SaveSnapshotCore(string destination)
    {
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); Open().BackupDatabase(target);
    }
    public void ReplaceSnapshot(string snapshot, Action applySettings, string? oldSnapshot = null, Action? prepare = null)
    {
        if (!_maintenance.TryEnterWriteLock(TimeSpan.FromSeconds(5))) throw new TimeoutException("Dictionary import/export is still running. Try restore again after it finishes.");
        var old = path + ".rollback-" + Guid.NewGuid().ToString("N");
        try
        {
            lock (_gate)
            {
                if (oldSnapshot is not null) SaveSnapshotCore(oldSnapshot);
                prepare?.Invoke();
                Close();
                var existed = File.Exists(path);
                if (existed) File.Move(path, old);
                try { File.Copy(snapshot, path); applySettings(); }
                catch
                {
                    Close(); if (File.Exists(path)) File.Delete(path);
                    if (existed) File.Move(old, path);
                    throw;
                }
                try { if (existed) File.Delete(old); } catch (IOException) { /* Committed; leave recovery copy for manual cleanup. */ }
            }
        }
        finally { _maintenance.ExitWriteLock(); }
    }

    public static (int Words, int Exclusions) ValidateSnapshot(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("Damaged dictionary snapshot.");
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type IN ('trigger','view') OR (type='table' AND name NOT IN ('words','excluded_words'))";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidDataException("Unsupported dictionary schema.");
        void Schema(string table, string[] columns)
        {
            command.CommandText = "PRAGMA table_info(" + table + ")";
            using var schema = command.ExecuteReader(); var index = 0;
            while (schema.Read())
            {
                if (index >= columns.Length || schema.GetString(1) != columns[index] || schema.GetString(2).ToUpperInvariant() != "TEXT" ||
                    schema.GetInt32(3) != 1 || schema.GetInt32(5) != index + 1) throw new InvalidDataException("Unsupported dictionary columns.");
                index++;
            }
            if (index != columns.Length) throw new InvalidDataException("Incomplete dictionary schema.");
        }
        Schema("words", ["word", "language"]); Schema("excluded_words", ["word"]);
        int Count(bool excluded)
        {
            command.CommandText = excluded ? "SELECT word,'' FROM excluded_words" : "SELECT word,language FROM words";
            using var rows = command.ExecuteReader(); var count = 0;
            while (rows.Read())
            {
                if (++count > 1_000_000) throw new InvalidDataException("Backup dictionary exceeds the entry limit.");
                var entry = new PersonalWord(rows.GetString(0), rows.GetString(1));
                var valid = excluded ? ValidateExclusion(entry) : Validate(entry);
                if (valid != entry) throw new InvalidDataException("Unnormalized dictionary entry.");
            }
            return count;
        }
        return (Count(false), Count(true));
    }
}
