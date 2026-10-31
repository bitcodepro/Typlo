using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly SemaphoreSlim _backupGate = new(1, 1);
    private readonly Lock _settingsLock = new();
    private volatile bool _configurationRecoveryRequired;
    private void EnsureConfigurationWritable()
    {
        if (_configurationRecoveryRequired) throw new InvalidOperationException("Restore recovery is incomplete. Restart Typlo before changing settings or the dictionary.");
    }
    private async Task<T> MutateDictionaryAsync<T>(Func<T> mutation)
    {
        await _backupGate.WaitAsync();
        try { EnsureConfigurationWritable(); return await Task.Run(mutation); }
        finally { _backupGate.Release(); }
    }
    private Task MutateDictionaryAsync(Action mutation) => MutateDictionaryAsync(() => { mutation(); return true; });
    private static void CleanupRecovery(string journal)
    {
        try { Directory.Delete(journal, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Finalized restore cleanup deferred to restart", e); }
    }
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
            EnsureConfigurationWritable();
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
                                    _personal.ReplaceSnapshot(staged.Database, () => ApplySettingsCore(staged.Settings, updateHistoryLimits: false, publish: false), Path.Combine(preparation, "personal.sqlite3"), () =>
                                    {
                                        new SettingsStore(Path.Combine(preparation, "settings.json")).Export(previous, Path.Combine(preparation, "settings.json"));
                                        Directory.Move(preparation, journal);
                                    });
                                    var published = Settings.Clone();
                                    BackupArchive.CompleteRecovery(journal);
                                    Volatile.Write(ref _publishedSettings, published);
                                }
                                catch (Exception restoreError)
                                {
                                    if (Directory.Exists(journal))
                                    {
                                        try
                                        {
                                            _personal.ReplaceSnapshot(Path.Combine(journal, "personal.sqlite3"), () => ApplySettingsCore(previous, updateHistoryLimits: false, publish: false));
                                            BackupArchive.CompleteRecovery(journal);
                                            CleanupRecovery(journal);
                                            _dictionaryFailed = false;
                                        }
                                        catch (Exception rollbackError)
                                        {
                                            _configurationRecoveryRequired = true; _dictionaryFailed = true;
                                            _platform.UnregisterAllHotkeys();
                                            throw new AggregateException("Restore and recovery failed. Restart Typlo before making further changes.", restoreError, rollbackError);
                                        }
                                    }
                                    throw;
                                }
                                // History is outside the archive/rollback. Enforce its limits only after commit.
                                try { Store.UpdateLimits(Limits(Settings)); }
                                catch (Exception e) { Log.Error("Committed restore history limits could not be enforced", e); }
                                CleanupRecovery(journal);
                                _dictionaryFailed = false;
                            }
                            finally { if (Directory.Exists(preparation)) Directory.Delete(preparation, true); }
                        }
                    });
                }
                finally { _convertGate.Release(); }
            }
            finally
            {
                if (!_configurationRecoveryRequired)
                { Volatile.Write(ref _updating, 0); if (_editingSettings) SuspendHotkeys(); else RegisterHotkeys(); }
            }
        }
        finally { _backupGate.Release(); }
    }
}
