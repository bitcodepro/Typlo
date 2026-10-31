using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Storage;

namespace Typlo.App.Services;

/// <summary>Windows resident process. Only UI children initialize Avalonia; all text tools run here.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BackgroundHost : IDisposable
{
    private readonly AppController _controller;
    private readonly WinPlatform _platform;
    private readonly AppPaths _paths;
    private readonly RuntimeChannel _runtime;
    private readonly IpcServer _commands;
    private readonly BackgroundTray _tray;
    private readonly UpdateService _updates;
    private readonly Lock _uiLock = new();
    private readonly SemaphoreSlim _uiCommands = new(1, 1);
    private Process? _ui;
    private long _settingsSession;
    private string? _uiPipe;
    private bool _stopping;
    private bool _installing;
    private readonly bool _diagnostic;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _updateProbe;
    private BackgroundHost(AppPaths paths, bool diagnostic)
    {
        _diagnostic = diagnostic; _paths = paths; Log.FilePath = paths.LogFile;
        _platform = new WinPlatform(monitorClipboard: !diagnostic);
        _controller = new AppController(_platform, paths, PlatformFactory.CreateKeyProtector(diagnostic), diagnostic);
        _runtime = new RuntimeChannel("Typlo.Runtime." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N"), DispatchAsync);
        _updates = new UpdateService(paths.DataDir); if (!diagnostic) UpdateService.CleanupAfterUpdate();
        if (diagnostic && Program.Argument("--report") is { } updateReport)
            _updates.RestartArguments = ["--background-probe", "--profile", paths.DataDir, "--report", updateReport, "--lifetime-seconds", "180", "--tray"];
        _tray = new BackgroundTray(Menu, () => ShowUi(["--settings"]), () => _platform.TrayTargetWindow);
        _updates.Changed += () => _tray.SetTip(_updates.Available is { } update ? Loc.T("update.tooltip", "v" + update.Version) : "Typlo");
        _updateProbe = diagnostic ? Task.CompletedTask : UpdateProbe.RunAsync(_updates, paths.DataDir, _stop.Token);
        _controller.ShowHistoryRequested += _ => ShowUi(["--history", "--target", ((long)_platform.TrayTargetWindow).ToString(CultureInfo.InvariantCulture)]);
        _controller.ToolsRequested += choices => ShowUi(choices ? ["--choices"] : ["--tools"]);
        _controller.PersonalWordRequested += (word, language) => ShowUi(["--add-word", word, "--word-language", language ?? ""]);
        _controller.ScreenshotRequested += destination => ShowUi(destination == Core.Imaging.CaptureDestination.File ? ["--capture", _controller.Settings.ScreenshotShape == Core.Imaging.CaptureShape.Freehand ? "freehand" : "rectangle", "--save"] : ["--capture", _controller.Settings.ScreenshotShape == Core.Imaging.CaptureShape.Freehand ? "freehand" : "rectangle"]);
        _commands = new IpcServer(Command, diagnostic ? _runtime.Name + ".commands" : null);
        Log.Info($"Background runtime v{UpdateService.CurrentVersion}, PID {Environment.ProcessId}");
    }
    internal static int Run(string[] args, AppPaths paths, bool diagnostic)
    {
        try
        {
            using var host = new BackgroundHost(paths, diagnostic);
            if (diagnostic)
            {
                var report = Program.Argument("--report");
                if (report is not null) File.WriteAllText(report, $"pid={Environment.ProcessId}\nchannel={host._runtime.Name}\ncommands={host._runtime.Name}.commands\n");
                if (int.TryParse(Program.Argument("--lifetime-seconds"), out var seconds) && seconds > 0)
                    _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => host._tray.Stop());
            }
            if (args.Contains("--history") || args.Contains("--settings") || args.Contains("--capture") || args.Contains("--shortcuts") || args.Contains("--dictionary") || args.Contains("--tools") || args.Contains("--choices")) host.Command(args);
            else if (!diagnostic && (!File.Exists(paths.SettingsFile + ".seen") && !args.Contains("--tray") || host._controller.FailedHotkeys.Count > 0))
            { File.WriteAllText(paths.SettingsFile + ".seen", ""); host.ShowUi(["--settings"]); }
            host._tray.Run(); return 0;
        }
        catch (Exception e) { Log.Error("Background startup failed", e); return 1; }
    }
    private RuntimeReply State()
    {
        int pid = 0;
        string? uiPipe = null;
        lock (_uiLock)
        {
            var child = _ui;
            // HasExited can synchronously raise Exited; its handler clears/disposes _ui.
            try { if (child is not null && !child.HasExited && ReferenceEquals(_ui, child)) { pid = child.Id; uiPipe = _uiPipe; } }
            catch (InvalidOperationException) { }
        }
        var (count, _) = _controller.Store.Stats();
        return new(true, FailedHotkeys: _controller.FailedHotkeys.ToArray(), PausedUntil: _controller.PausedUntil, UiProcessId: pid, StoredItems: count, AvailableVersion: _updates.Available?.Version.ToString(), UiChannel: uiPipe);
    }
    private Task<RuntimeReply> DispatchAsync(RuntimeRequest command)
    {
        if (command.Command is "auto-status" or "dictionary-check" or "personal-query" or "personal-edit" or "personal-add" or "personal-import" or "personal-export" or "personal-word" or "excluded-word" or "backup-export" or "backup-preview" or "backup-restore" or "tools-state")
            return DispatchDictionaryAsync(command);
        if (_diagnostic && command.Command == "register-hotkeys" && File.Exists(Path.Combine(_paths.DataDir, "hold-hotkey-restore")))
            return HoldHotkeyRestoreAsync(command);
        return DispatchCore(command);
    }
    private async Task<RuntimeReply> DispatchDictionaryAsync(RuntimeRequest command)
    {
        try { return await DispatchCore(command); }
        catch (Exception e) when (command.Command.StartsWith("backup-", StringComparison.Ordinal) &&
            (e is System.Text.Json.JsonException or InvalidDataException or Typlo.Core.Settings.SettingsImportException or ArgumentException ||
             e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 }))
        { return new(false, Loc.T("tools.backupInvalid")); }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19 && command.Command == "personal-edit") { return new(false, Loc.T("manage.duplicate")); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException || (e is ArgumentException && command.Command == "personal-import"))
        { return new(false, Loc.T("manage.invalidFile")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return new(false, Loc.T("manage.fileError")); }
        catch (ArgumentException) { return new(false, Loc.T(command.Command == "personal-export" ? "manage.fileError" : "dictionary.invalid")); }
        catch (InvalidOperationException) { return new(false, Loc.T("manage.changed")); }
        catch (TimeoutException) { return new(false, Loc.T("reason.Busy")); }
    }
    private async Task<RuntimeReply> HoldHotkeyRestoreAsync(RuntimeRequest command)
    {
        var hold = Path.Combine(_paths.DataDir, "hold-hotkey-restore");
        File.WriteAllText(Path.Combine(_paths.DataDir, "hotkey-restore-waiting"), "waiting");
        var limit = Stopwatch.StartNew();
        while (File.Exists(hold))
        {
            if (limit.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Hotkey restore fixture was not released");
            await Task.Delay(20, _stop.Token);
        }
        var reply = await DispatchCore(command);
        File.WriteAllText(Path.Combine(_paths.DataDir, "hotkey-restore-complete"), "complete");
        return reply;
    }
    private Task<RuntimeReply> DispatchCore(RuntimeRequest command)
    {
        lock (_uiLock)
        {
            if (command.UiOwner > 0)
            {
                var child = _ui;
                try
                {
                    if (child is null || child.HasExited || !ReferenceEquals(_ui, child) || child.Id != command.UiOwner)
                        return Task.FromResult(new RuntimeReply(false, "UI session ended"));
                }
                catch (InvalidOperationException) { return Task.FromResult(new RuntimeReply(false, "UI session ended")); }
            }
            if (command.UiOwner == 0 && command.Command is "capture-begin" or "capture-end" or "image-write-begin" or "image-write-end")
                return Task.FromResult(new RuntimeReply(false, "No active UI session"));
            // Synchronous effects (suspend/register/lease acquisition) finish under the same lock as UI exit.
            // Paste may continue asynchronously in the resident after the originating UI has closed.
            return ExecuteAsync(command);
        }
    }
    private async Task<RuntimeReply> ExecuteAsync(RuntimeRequest command)
    {
        switch (command.Command)
        {
            case "history-target-fixture" when _diagnostic:
                var target = _platform.TrayTargetWindow;
                Menu()[0].Click!(target);
                return new(true, Window: (long)target);
            case "image-fixture" when _diagnostic:
                var crypto = ClipCrypto.LoadOrCreate(_paths.KeyFile, PlatformFactory.CreateKeyProtector(diagnostic: true));
                var fixture = Path.Combine(_paths.DataDir, "fixture.image");
                _controller.IngestDiagnosticImage(crypto.Decrypt(File.ReadAllBytes(fixture)));
                return new(true);
            case "update-fixture" when _diagnostic:
                await UpdateProbe.RunAsync(_updates, _paths.DataDir, _stop.Token, immediately: true);
                return new(_updates.Available is not null);
            case "dictionary-fixture" when _diagnostic:
                _controller.OpenDictionaryForDiagnostic(); return new(_controller.DictionaryIsOpen);
            case "state": return State();
            case "update-start": return BeginInstall();
            case "update-state": return UpdateState();
            case "tools-state": return new(true, Tools: await _controller.GetToolsStatusAsync());
            case "backup-export": await _controller.ExportBackupAsync(command.Text ?? ""); return new(true);
            case "backup-preview": return new(true, Backup: await _controller.PreviewBackupAsync(command.Text ?? ""));
            case "backup-restore": await _controller.RestoreBackupAsync(command.Text ?? "", command.Args?.FirstOrDefault() ?? ""); return State();
            case "auto-status": return new(true, AutoCorrection: await _controller.GetAutomaticStatusAsync());
            case "dictionary-check": return new(true, AutoCorrection: await _controller.CheckDictionaryAsync());
            case "personal-query":
                if (command.WordQuery is null) return new(false, "Missing dictionary query");
                return new(true, WordPage: await _controller.QueryPersonalWordsAsync(command.WordQuery));
            case "personal-edit":
                if (command.OriginalWord is null || command.ReplacementWord is null) return new(false, "Missing dictionary entry");
                await _controller.EditPersonalWordAsync(command.OriginalWord, command.ReplacementWord, command.Flag); return new(true);
            case "personal-add":
                if (command.Words is null) return new(false, "Missing words");
                return new(true, ImportResult: await _controller.AddPersonalWordsAsync(command.Words, command.Flag));
            case "personal-import":
                if (command.Text is null) return new(false, "Missing import file");
                return new(true, ImportResult: await _controller.ImportPersonalWordsAsync(command.Text, command.Flag));
            case "personal-export":
                if (command.Text is null) return new(false, "Missing export file");
                await _controller.ExportPersonalWordsAsync(command.Text, command.Flag); return new(true);
            case "excluded-word":
                if (command.Text is null) return new(false, Loc.T("dictionary.invalid"));
                await _controller.SetExcludedWordAsync(command.Text, command.Flag); return new(true);
            case "personal-word":
                if (command.Text is null || command.Args is not [var language]) return new(false, Loc.T("dictionary.invalid"));
                await _controller.SetPersonalWordAsync(command.Text, language, command.Flag); return new(true);
            case "settings": if (command.Settings is null) return new(false, "Missing settings"); _controller.ApplySettings(command.Settings); return State();
            case "refresh-layouts": _controller.RefreshKeyboardLayouts(); return State();
            case "suspend-hotkeys":
                if (command.Id < _settingsSession) return State();
                _settingsSession = command.Id;
                _controller.SuspendHotkeys(); return State();
            case "register-hotkeys":
                // A close from an older window must not undo a newer window's suspension.
                if (command.Id == _settingsSession) _controller.RegisterHotkeys();
                return State();
            case "probe-hotkey":
                var hotkey = Hotkey.TryParse(command.Text ?? "");
                return new(hotkey is { IsUsable: true } && _platform.RegisterHotkey(9000, hotkey));
            case "clear-probes": _platform.UnregisterAllHotkeys(); return new(true);
            case "paste": await _controller.PasteAsync(command.Id, (nint)command.Window, command.Text != "copy", command.Flag); return new(true);
            case "paste-previous": await _controller.PastePreviousAsync((nint)command.Window, command.Flag); return new(true);
            case "capture-begin": return await _controller.BeginExternalCaptureAsync() ? State() : new(false, "Another clipboard operation is running");
            case "image-write-begin": _controller.BeginExternalClipboardWrite(); return new(true);
            case "image-write-end": _controller.EndExternalClipboardWrite(); return new(true);
            case "capture-end": _controller.EndExternalCapture(); return new(true);
            default: return new(false, "Unknown runtime command");
        }
    }
    private IReadOnlyList<BackgroundTray.Item> Menu()
    {
        var items = new List<BackgroundTray.Item>
        {
            new(Loc.T("tray.history"),window=>ShowUi(["--history","--target",((long)window).ToString(CultureInfo.InvariantCulture)])),
            new(Loc.T("tray.settings"),_=>ShowUi(["--settings"])),new(Loc.T("shortcuts.menu"),_=>ShowUi(["--shortcuts"])),new(""),
            new(Loc.T("tools.title"), _ => ShowUi(["--tools"])),
            new(Loc.T("manage.title"), _ => ShowUi(["--dictionary"])),
            new(Loc.T("capture.copy"),_=>ShowUi(["--capture",_controller.Settings.ScreenshotShape==Core.Imaging.CaptureShape.Freehand?"freehand":"rectangle"])),
            new(Loc.T("capture.save"),_=>ShowUi(["--capture",_controller.Settings.ScreenshotShape==Core.Imaging.CaptureShape.Freehand?"freehand":"rectangle","--save"])),new(""),
            new(Loc.T("tray.keepAwake"),_=>_controller.SetPreventScreenLock(!_controller.Settings.PreventScreenLock),_controller.Settings.PreventScreenLock),
        };
        if (_controller.IsPaused) items.Add(new(_controller.PausedUntil == DateTimeOffset.MaxValue ? Loc.T("tray.resumeRestart") : Loc.T("tray.resumeUntil", _controller.PausedUntil!.Value.LocalDateTime), _ => _controller.ResumeHistory()));
        else
        {
            items.Add(new(Loc.T("tray.pause10"), _ => _controller.PauseHistory(TimeSpan.FromMinutes(10))));
            items.Add(new(Loc.T("tray.pause60"), _ => _controller.PauseHistory(TimeSpan.FromHours(1))));
            items.Add(new(Loc.T("tray.pauseRestart"), _ => _controller.PauseHistory(null)));
        }
        items.Add(new(""));
        items.Add(new(_updates.Available is { } u ? Loc.T("update.install", u.Version) : Loc.T("update.check"), _ => ShowUi(["--update"])));
        items.Add(new(Loc.T("tray.exit"), _ => _tray.Stop())); return items;
    }
    private void Command(string[] args)
    {
        if (args.Contains("--shutdown")) { _tray.Stop(); return; }
        if (args.Contains("--install-update")) { BeginInstall(); return; }
        if (Array.IndexOf(args, "--convert") is var i and >= 0)
        {
            var target = i + 1 < args.Length ? args[i + 1] : "toggle"; var layout = _controller.FindLayout(target);
            if (target == "toggle" || layout is not null) _ = _controller.ConvertSelectionAsync(layout); return;
        }
        if (args.Contains("--history") && !args.Contains("--target")) args = [.. args, "--target", ((long)_platform.TrayTargetWindow).ToString(CultureInfo.InvariantCulture)];
        ShowUi(args.Length == 0 ? ["--settings"] : args);
    }
    private RuntimeReply UpdateState()
    {
        lock (_uiLock) return new(true, UpdateInstalling: _installing, UpdateProgress: _updates.Progress, UpdateError: _updates.LastError);
    }
    private RuntimeReply BeginInstall()
    {
        lock (_uiLock)
        {
            if (_stopping) return new(false, "Application is shutting down");
            if (_installing) return UpdateState();
            _updates.ReportInstallError(null);
            _installing = true;
            _ = Task.Run(InstallUpdateAsync);
            return UpdateState();
        }
    }
    private async Task InstallUpdateAsync()
    {
        var succeeded = false;
        try
        {
            var available = await _updates.CheckAsync();
            if (_updates.LastCheckError is { } checkError) { _updates.ReportInstallError(checkError); return; }
            if (available is null) { _updates.ReportInstallError(Loc.T("update.upToDate")); return; }
            succeeded = await _updates.InstallAsync(async replace => { CloseUi(); await _controller.ReplaceUpdateFilesAsync(replace); });
            if (succeeded) _tray.Stop();
        }
        catch (Exception e) { Log.Error("Background update failed", e); _updates.ReportInstallError(e.Message); }
        finally
        {
            lock (_uiLock) _installing = false;
            // Swap errors can occur after the UI has released its libraries. Recreate it
            // with the resident's error instead of leaving a silent, apparently dead button.
            if (!succeeded) ShowUi(["--update-failed"]);
        }
    }
    private void ShowUi(string[] args) => _ = SendUiAsync(args);
    private async Task SendUiAsync(string[] args)
    {
        await _uiCommands.WaitAsync();
        try
        {
            Process? current;
            string? pipe;
            lock (_uiLock) { if (_stopping || _installing) return; current = _ui; pipe = _uiPipe; }
            if (current is not null)
            {
                try { await new RuntimeClient(pipe!).CallAsync(new("ui", Args: args), 3000); return; }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    try { if (!current.WaitForExit(500)) { Log.Error("UI is not responding", e); return; } }
                    catch (InvalidOperationException) { }
                }
            }
            lock (_uiLock)
            {
                if (_stopping || _installing) return;
                // A local trace connected to the previous UI's endpoint after a new PID was
                // published. Give each generation a distinct name, regardless of handle lifetime.
                var uiPipe = "Typlo.Ui." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Typlo.dll"));
                foreach (var arg in new[] { "--ui", "--runtime-channel", _runtime.Name, "--ui-pipe", uiPipe, "--profile", _paths.DataDir, "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
                if (_diagnostic) start.ArgumentList.Add("--diagnostic-ui");
                foreach (var arg in args) start.ArgumentList.Add(arg);
                var child = Process.Start(start) ?? throw new IOException("Cannot start UI"); _ui = child; _uiPipe = uiPipe;
                _settingsSession = 0;
                _platform.UiProcessId = child.Id;
                child.EnableRaisingEvents = true;
                child.Exited += (_, _) =>
                {
                    lock (_uiLock)
                    {
                        if (!ReferenceEquals(_ui, child)) return;
                        _ui = null; _uiPipe = null; _platform.UiProcessId = 0; _controller.EndExternalCapture();
                        if (!_stopping) _controller.RegisterHotkeys();
                        child.Dispose();
                    }
                };
                if (child.HasExited) { _ui = null; _uiPipe = null; _platform.UiProcessId = 0; _controller.EndExternalCapture(); _controller.RegisterHotkeys(); child.Dispose(); }
            }
        }
        catch (Exception e) { Log.Error("Cannot open UI", e); }
        finally { _uiCommands.Release(); }
    }
    private void CloseUi()
    {
        Process? child; string? pipe; lock (_uiLock) { child = _ui; pipe = _uiPipe; }
        if (child is null) return;
        try
        {
            if (!child.HasExited)
            {
                try { new RuntimeClient(pipe!).Call(new("ui", Args: ["--shutdown"]), 1000); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
                if (!child.WaitForExit(3000)) child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        lock (_uiLock) _stopping = true;
        _stop.Cancel(); _updateProbe.GetAwaiter().GetResult();
        CloseUi(); _commands.Dispose(); _runtime.Dispose(); _updates.Dispose(); _tray.Dispose(); _controller.Dispose(); _platform.Dispose();
    }
}
