using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;

namespace Typlo.App;

public partial class App
{
    private RuntimeChannel? _uiChannel;
    private RuntimeClient? _remoteRuntime;

    private void StartRemoteUi(IClassicDesktopStyleApplicationLifetime desktop, AppPaths paths)
    {
        try
        {
            Log.FilePath = Path.Combine(paths.DataDir, "typlo-ui.log");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var channel = Program.Argument("--runtime-channel") ?? throw new ArgumentException("Missing runtime channel");
            var uiPipe = Program.Argument("--ui-pipe") ?? throw new ArgumentException("Missing UI channel");
            var client = new RuntimeClient(channel, Environment.ProcessId);
            _remoteRuntime = client;
            var platform = new WinPlatform(passive: true);
            _platform = platform;
            _controller = AppController.CreateRemote(platform, paths, PlatformFactory.CreateKeyProtector(), client);
            platform.HotkeyProbe = (_, hotkey) =>
            {
                try { return client.Call(new("probe-hotkey", Text: hotkey.ToString())).Ok; }
                catch (IOException) { return false; }
            };
            platform.ClearProbes = () => client.Call(new("clear-probes"));
            ApplyTheme();
            var language = Loc.Current;
            _controller.SettingsChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (language != Loc.Current) { language = Loc.Current; _historyWindow?.Close(); _historyWindow = null; }
                ApplyTheme();
            });
            _updates = new UpdateService(paths.DataDir);
            _screenCapture = new ScreenCaptureService(_controller);
            var lastRequest = Environment.TickCount64;
            var shuttingDown = false;
            var diagnosticUi = Program.Args.Contains("--diagnostic-ui");
            void TraceUi(string phase, long trace = 0, string action = "none")
            {
                if (diagnosticUi) Log.Info($"UI lifecycle pid={Environment.ProcessId} tid={Platform.Windows.Native.GetCurrentThreadId()} phase={phase} trace={trace} action={action} stopping={Volatile.Read(ref shuttingDown)}");
            }
            async void Shutdown()
            {
                if (shuttingDown) return;
                shuttingDown = true;
                TraceUi("shutdown-start");
                await UiOperations.Shared.StopAsync();
                TraceUi("shutdown-drained");
                desktop.Shutdown();
            }
            void Command(string[] args)
            {
                Interlocked.Exchange(ref lastRequest, Environment.TickCount64);
                if (args.Contains("--shutdown")) { Shutdown(); return; }
                if (args.Contains("--dismiss"))
                {
                    _screenCapture.CancelSelection();
                    foreach (var window in desktop.Windows.ToArray())
                    {
                        TraceUi("close-begin", action: window.GetType().Name);
                        window.Close();
                        TraceUi("close-end", action: window.GetType().Name);
                    }
                    return;
                }
                if (args.Contains("--ocr-fixture") && Program.Args.Contains("--diagnostic-ui"))
                {
                    if (_historyWindow?.IsVisible != true) ShowHistory(0);
                    _ = OcrFixtureAsync();
                    return;
                }
                if (args.Contains("--history"))
                {
                    var index = Array.IndexOf(args, "--target");
                    var target = index >= 0 && index + 1 < args.Length && long.TryParse(args[index + 1], out var value) ? (nint)value : 0;
                    ShowHistory(target);
                }
                else if (args.Contains("--shortcuts")) ShowShortcuts();
                else if (args.Contains("--dictionary")) ShowDictionary();
                else if (args.Contains("--update")) ShowUpdateDialog();
                else if (args.Contains("--update-failed")) _ = ShowUpdateFailureAsync();
                else if (Array.IndexOf(args, "--add-word") is var wordIndex and >= 0)
                {
                    var languageIndex = Array.IndexOf(args, "--word-language");
                    ShowPersonalWord(wordIndex + 1 < args.Length ? args[wordIndex + 1] : "", languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1] : null);
                }
                else if (args.Contains("--capture")) OnCommand(args);
                else ShowSettings();
            }
            async Task OcrFixtureAsync()
            {
                try
                {
                    await _historyWindow!.CopyTextFromImageAsync(async (png, token) =>
                    {
                        File.WriteAllText(Path.Combine(paths.DataDir, "ocr-started"), "started");
                        try
                        {
                            await OcrService.RunAsync(new("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                                "$m = [IO.MemoryStream]::new(); [Console]::OpenStandardInput().CopyTo($m); Start-Sleep -Seconds 4" } }, png, token);
                        }
                        catch (OperationCanceledException)
                        { File.WriteAllText(Path.Combine(paths.DataDir, "ocr-canceled"), "canceled"); throw; }
                        return null; // Exercise the real OCR lifetime without changing the user's clipboard.
                    });
                    File.WriteAllText(Path.Combine(paths.DataDir, "ocr-completed"), "completed");
                }
                catch (Exception e) { Log.Error("OCR fixture failed", e); }
            }
            async Task ShowUpdateFailureAsync()
            {
                try
                {
                    var state = await client.CallAsync(new("update-state"));
                    ShowUpdateDialog(state.UpdateError ?? state.Error ?? Loc.T("update.failed"));
                }
                catch (Exception e) { ShowUpdateDialog(e.Message); }
            }
            _uiChannel = new RuntimeChannel(uiPipe, async request =>
            {
                if (diagnosticUi && request.Command == "dictionary-manager-fixture")
                    return await Dispatcher.UIThread.InvokeAsync(() => new RuntimeReply(_dictionaryWindow?.IsVisible == true,
                        WordPage: _dictionaryWindow?.DisplayedPage, AutoCorrection: _dictionaryWindow?.DisplayedStatus));
                if (diagnosticUi && request.Command == "update-dialog-fixture")
                    return await Dispatcher.UIThread.InvokeAsync(() => new RuntimeReply(_updateDialog?.IsVisible == true,
                        Error: _updateDialog?.DisplayedError, AvailableVersion: _updates.Available?.Version.ToString()));
                if (diagnosticUi && request.Command == "update-install-fixture")
                    return await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_updateDialog is null) return new RuntimeReply(false, "No update dialog");
                        _ = _updateDialog.OnPrimaryAsync(); return new RuntimeReply(true);
                    });
                var action = request.Command == "history-target-fixture" ? "target-query" :
                    request.Args?.Contains("--dismiss") == true ? "dismiss" : request.Args?.Contains("--settings") == true ? "settings" :
                    request.Args?.Contains("--history") == true ? "history" : "other";
                TraceUi("queued", request.Trace, action);
                if (request.Command == "history-target-fixture" && Program.Args.Contains("--diagnostic-ui"))
                    return await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        TraceUi("query", request.Trace, action);
                        return new RuntimeReply(true, Window: (long)(_historyWindow?.TargetWindow ?? 0));
                    });
                if (request.Command != "ui" || request.Args is null) return new(false, "Unknown UI command");
                Interlocked.Exchange(ref lastRequest, Environment.TickCount64);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TraceUi("begin", request.Trace, action);
                    Command(request.Args);
                    TraceUi("end", request.Trace, action);
                });
                return new(true);
            });
            var parent = Process.GetProcessById(int.Parse(Program.Argument("--parent-pid") ?? throw new ArgumentException("Missing parent")));
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => { client.Disconnect(); Dispatcher.UIThread.Post(Shutdown); };
            if (parent.HasExited) { parent.Dispose(); Shutdown(); return; }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                _controller.Store.CheckExternalChanges();
                if (desktop.Windows.Any(w => w.IsVisible) || _screenCapture.IsActive || client.IsBusy || UiOperations.Shared.IsBusy)
                    Interlocked.Exchange(ref lastRequest, Environment.TickCount64);
                else if (Environment.TickCount64 - Interlocked.Read(ref lastRequest) > 1000) Shutdown();
            };
            desktop.Exit += (_, _) => { timer.Stop(); parent.Dispose(); _uiChannel.Dispose(); Cleanup(); };
            timer.Start();
            Command(Program.Args);
            TraceUi("startup-complete");
        }
        catch (Exception e) { Log.Error("UI startup failed", e); _uiChannel?.Dispose(); Cleanup(); desktop.Shutdown(1); }
    }
}
