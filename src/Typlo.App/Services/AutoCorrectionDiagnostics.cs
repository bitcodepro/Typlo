using System.Diagnostics;

namespace Typlo.App.Services;

public enum DictionaryHealth { NotChecked, Ready, Missing, Failed }
public enum AutoCorrectionReason { Corrected, Disabled, Unsupported, Paused, DictionaryUnavailable, Canceled, ExcludedApplication, UnknownApplication, UnsupportedLanguage, ProtectedToken, UnknownWord, AlreadyCorrect, LanguageRestricted, Ambiguous, LowConfidence, Busy, CopyUnavailable, ClipboardChanged, Unconfirmed, Failed, SettingsOpen, ScreenCapture, Updating, DictionaryChecking, ExcludedWord, UndoSuppressed }
public sealed record AutoCorrectionEntry(DateTimeOffset At, AutoCorrectionReason Reason, long Milliseconds, string? Source = null, string? Target = null);
public sealed record AutoCorrectionStatus(bool Enabled, bool Supported, DictionaryHealth Health, AutoCorrectionEntry[] Recent, AutoCorrectionReason? PauseReason = null);

public static class AutoCorrectionReport
{
    public static string Format(AutoCorrectionStatus status) =>
        $"Typlo {UpdateService.CurrentVersion}\nEnabled: {status.Enabled}\nSupported: {status.Supported}\nDictionary: {status.Health}\nPause: {status.PauseReason?.ToString() ?? "-"}\n" +
        string.Join("\n", status.Recent.Select(row => $"{row.At:O} {row.Reason} {row.Milliseconds}ms {row.Source ?? "-"} -> {row.Target ?? "-"}"));
}

public sealed partial class AppController
{
    private readonly Lock _diagnosticsLock = new();
    private readonly Queue<AutoCorrectionEntry> _autoDiagnostics = new();
    private DictionaryHealth _dictionaryHealth;
    private int _checkingDictionary;
    private AutoCorrectionReason? AutomaticPauseReason() => _editingSettings ? AutoCorrectionReason.SettingsOpen :
        Volatile.Read(ref _updating) != 0 ? AutoCorrectionReason.Updating : _capturingScreen ? AutoCorrectionReason.ScreenCapture :
        Volatile.Read(ref _checkingDictionary) != 0 ? AutoCorrectionReason.DictionaryChecking : null;

    private void RecordAutomaticResult(AutomaticWord word)
    {
        var entry = new AutoCorrectionEntry(DateTimeOffset.UtcNow, word.Outcome, (long)Stopwatch.GetElapsedTime(word.Started).TotalMilliseconds, word.SourceLanguage, word.TargetLanguage);
        lock (_diagnosticsLock) { if (_autoDiagnostics.Count == 20) _autoDiagnostics.Dequeue(); _autoDiagnostics.Enqueue(entry); }
    }

    internal AutoCorrectionStatus AutomaticStatus()
    {
        // The keyboard callback uses the same small ring lock. Never hold it across filesystem I/O.
        var available = DictionaryAvailable;
        var enabled = Settings.AutoCorrectEnabled; var supported = _platform.SupportsAutomaticWordCorrection;
        var pause = AutomaticPauseReason();
        lock (_diagnosticsLock)
            return new(enabled, supported, !available ? DictionaryHealth.Missing : _dictionaryHealth, _autoDiagnostics.Reverse().ToArray(), pause);
    }

    public async Task<AutoCorrectionStatus> GetAutomaticStatusAsync()
    {
        if (_remote is not null) return (await _remote.CallAsync(new("auto-status"))).AutoCorrection ?? throw new IOException("Missing dictionary status");
        return await Task.Run(AutomaticStatus);
    }

    public async Task<AutoCorrectionStatus> CheckDictionaryAsync()
    {
        if (_remote is not null) return (await _remote.CallAsync(new("dictionary-check"))).AutoCorrection ?? throw new IOException("Missing dictionary status");
        if (Interlocked.CompareExchange(ref _checkingDictionary, 1, 0) != 0) return await Task.Run(AutomaticStatus);
        try
        {
            await StopPendingAutomaticWords();
            if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("Another text operation is running.");
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _updating) != 0) throw new InvalidOperationException("Dictionary is busy.");
                await Task.Run(() =>
                {
                    try
                    {
                        _lexicon.Close(); _personal.Close(); _lexicon.Lookup("hello"); _personal.Query(new(PageSize: 1)); _dictionaryFailed = false;
                        lock (_diagnosticsLock) _dictionaryHealth = DictionaryHealth.Ready;
                    }
                    catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException or UnauthorizedAccessException)
                    {
                        _lexicon.Close(); _dictionaryFailed = true;
                        var health = DictionaryAvailable ? DictionaryHealth.Failed : DictionaryHealth.Missing;
                        lock (_diagnosticsLock) _dictionaryHealth = health;
                    }
                });
            }
            finally { _convertGate.Release(); }
        }
        finally { Volatile.Write(ref _checkingDictionary, 0); }
        return await Task.Run(AutomaticStatus);
    }
}
