using System.Text.Json;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly SemaphoreSlim _backupGate = new(1, 1);
    private readonly Lock _settingsLock = new();
    public async Task ExportBackupAsync(string file)
    {
        if (_remote is not null) { await _remote.CallAsync(new("backup-export", Text: file), 120_000); return; }
        await _backupGate.WaitAsync();
        try
        {
            AppSettings snapshot; lock (_settingsLock) snapshot = Settings.Clone();
            await Task.Run(() => BackupArchive.Write(file, snapshot, _personal));
        }
        finally { _backupGate.Release(); }
    }
    public async Task<BackupInfo> PreviewBackupAsync(string file)
    {
        if (_remote is not null) return (await _remote.CallAsync(new("backup-preview", Text: file), 120_000)).Backup ?? throw new IOException("Missing backup preview");
        return await Task.Run(() => { using var staged = BackupArchive.Read(file, _dictionaryDataDir); return staged.Info; });
    }
    public async Task RestoreBackupAsync(string file, string expectedHash)
    {
        if (_remote is not null) { UpdateRemoteState(await _remote.CallAsync(new("backup-restore", Text: file, Args: [expectedHash]), 120_000)); return; }
        await _backupGate.WaitAsync();
        try
        {
            using var staged = await Task.Run(() => BackupArchive.Read(file, _dictionaryDataDir));
            if (!string.Equals(staged.Info.Hash, expectedHash, StringComparison.Ordinal)) throw new InvalidDataException("Backup changed after preview; inspect it again.");
            if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) throw new InvalidOperationException("Application maintenance is already running.");
            try
            {
                await StopPendingAutomaticWords(); CancelChoices();
                if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("A text operation is running.");
                try
                {
                    await Task.Run(() =>
                    {
                        lock (_settingsLock)
                        {
                            var previous = Settings.Clone();
                            var journal = BackupArchive.RecoveryDirectory(_dictionaryDataDir);
                            if (Directory.Exists(journal)) throw new IOException("A previous restore needs recovery. Restart Typlo.");
                            var preparation = Path.Combine(_dictionaryDataDir, ".restore-prepare-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(preparation);
                            try
                            {
                                try
                                {
                                    _personal.ReplaceSnapshot(staged.Database, () => ApplySettings(staged.Settings), Path.Combine(preparation, "personal.sqlite3"), () =>
                                    {
                                        new SettingsStore(Path.Combine(preparation, "settings.json")).Export(previous, Path.Combine(preparation, "settings.json"));
                                        Directory.Move(preparation, journal);
                                    });
                                    File.WriteAllText(Path.Combine(journal, "committed"), "ok");
                                }
                                catch
                                {
                                    _dictionaryFailed = true;
                                    if (Directory.Exists(journal))
                                        _personal.ReplaceSnapshot(Path.Combine(journal, "personal.sqlite3"), () => ApplySettings(previous));
                                    else ApplySettings(previous);
                                    throw;
                                }
                                try { Directory.Delete(journal, true); }
                                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Committed restore cleanup deferred to restart", e); }
                                _dictionaryFailed = false;
                            }
                            finally { if (Directory.Exists(preparation)) Directory.Delete(preparation, true); }
                        }
                    });
                }
                finally { _convertGate.Release(); }
            }
            finally { Volatile.Write(ref _updating, 0); if (_editingSettings) SuspendHotkeys(); else RegisterHotkeys(); }
        }
        finally { _backupGate.Release(); }
    }
}
