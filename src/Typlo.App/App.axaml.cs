using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Services;
using Typlo.App.Views;
using Typlo.Core;
using Typlo.Core.Imaging;

namespace Typlo.App;

public partial class App : Application
{
    private AppController? _controller;
    private IPlatform? _platform;
    private ClipboardWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private ScreenCaptureService? _screenCapture;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var paths = Program.DataPaths;
        Log.FilePath = paths.LogFile;

        var selftest = Array.IndexOf(Program.Args, "--selftest");
        if (selftest >= 0)
        {
            var report = selftest + 1 < Program.Args.Length ? Program.Args[selftest + 1] : "selftest.txt";
            Task.Run(async () =>
            {
                var code = await SelfTest.RunAsync(report);
                Dispatcher.UIThread.Post(() => desktop.Shutdown(code));
            });
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var shots = Array.IndexOf(Program.Args, "--screenshots");
        if (shots >= 0)
        {
            var dir = shots + 1 < Program.Args.Length ? Program.Args[shots + 1] : "screenshots";
            Dispatcher.UIThread.Post(async () =>
            {
                try { await ScreenshotMode.RunAsync(dir); }
                catch (Exception e) { Log.Error("Screenshots failed", e); }
                desktop.Shutdown();
            });
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (Program.IsRemoteUi) { StartRemoteUi(desktop, paths); base.OnFrameworkInitializationCompleted(); return; }

        try
        {
            _platform = PlatformFactory.Create();
            _controller = new AppController(_platform, paths, PlatformFactory.CreateKeyProtector());
        }
        catch (Exception e)
        {
            Log.Error("Startup failed", e);
            desktop.Shutdown(1);
            return;
        }
        Log.Info($"Started v{UpdateService.CurrentVersion} on {_platform.Name}, data in {paths.DataDir}");
        if (_platform.Capabilities.Notes is { } notes) Log.Info(notes);
        _ipc = new IpcServer(OnCommand);

        UpdateService.CleanupAfterUpdate();
        _updates = new UpdateService(paths.DataDir);
        _updates.Changed += () => Dispatcher.UIThread.Post(RefreshTray);
        _updates.Start();

        _controller.ShowHistoryRequested += prev => Dispatcher.UIThread.Post(() => ShowHistory(prev));
        _controller.PersonalWordRequested += (word, language) => Dispatcher.UIThread.Post(() => ShowPersonalWord(word, language));
        _screenCapture = new ScreenCaptureService(_controller);
        _controller.ScreenshotRequested += destination => Dispatcher.UIThread.Post(() => _ = _screenCapture.StartAsync(_controller.Settings.ScreenshotShape, destination));
        CreateTray();
        desktop.Exit += (_, _) => Cleanup();

        if (Program.Args.Contains("--capture")) OnCommand(Program.Args);
        else if (_controller.FailedHotkeys.Count > 0 || !Program.Args.Contains("--tray") && !File.Exists(paths.SettingsFile + ".seen"))
        {
            File.WriteAllText(paths.SettingsFile + ".seen", "");
            ShowSettings();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private IpcServer? _ipc;
    private UpdateService? _updates;

    /// <summary>Commands from a second instance (e.g. a desktop shortcut running "Typlo --history").</summary>
    private void OnCommand(string[] args)
    {
        if (_controller is null) return;
        if (Array.IndexOf(args, "--capture") is var captureIndex and >= 0)
        {
            var shape = captureIndex + 1 < args.Length && args[captureIndex + 1] == "freehand" ? CaptureShape.Freehand : CaptureShape.Rectangle;
            Dispatcher.UIThread.Post(() => _ = _screenCapture!.StartAsync(shape, args.Contains("--save") ? CaptureDestination.File : CaptureDestination.Clipboard));
        }
        else if (args.Contains("--history"))
            Dispatcher.UIThread.Post(() => ShowHistory(_platform!.ForegroundWindow));
        else if (args.Contains("--install-update"))
            _ = Task.Run(async () =>
            {
                if (_updates is null || await _updates.CheckAsync() is null) return;
                await InstallUpdateAsync();
            });
        else if (args.Contains("--settings"))
            Dispatcher.UIThread.Post(ShowSettings);
        else if (Array.IndexOf(args, "--convert") is var i and >= 0)
        {
            var target = i + 1 < args.Length ? args[i + 1] : "toggle";
            var layout = _controller.FindLayout(target);
            if (target == "toggle" || layout is not null) _ = _controller.ConvertSelectionAsync(layout);
        }
    }

    private void CreateTray()
    {
        _tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = "Typlo",
            Menu = BuildTrayMenu(),
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(this, [_tray]);

        var language = Loc.Current;
        ApplyTheme();
        _controller!.SettingsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            // Rebuild everything that shows text when the interface language changes.
            if (Loc.Current != language)
            {
                language = Loc.Current;
                _historyWindow?.Close();
                _historyWindow = null;
            }
            ApplyTheme();
            RefreshTray();
        });
    }

    private void ApplyTheme() => RequestedThemeVariant = _controller?.Settings.UiTheme switch
    {
        "light" => Avalonia.Styling.ThemeVariant.Light,
        "dark" => Avalonia.Styling.ThemeVariant.Dark,
        _ => Avalonia.Styling.ThemeVariant.Default,
    };

    private void RefreshTray()
    {
        if (_tray is null) return;
        _tray.Menu = BuildTrayMenu();
        _tray.ToolTipText = _updates?.Available is { } u ? Loc.T("update.tooltip", "v" + u.Version) : "Typlo";
    }

    private async Task InstallUpdateAsync()
    {
        if (Program.IsRemoteUi)
        {
            if (_updates is not null && _remoteRuntime is not null) await _updates.InstallRemoteAsync(_remoteRuntime);
            return;
        }
        if (_updates is null) return;
        if (await _updates.InstallAsync(replace => _controller!.ReplaceUpdateFilesAsync(replace)))
            Dispatcher.UIThread.Post(() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
    }

    /// <summary>"Pause history" submenu, or a "Resume" item while paused.</summary>
    private NativeMenuItem BuildPauseItem()
    {
        var controller = _controller!;
        if (controller.IsPaused)
        {
            var resume = new NativeMenuItem(controller.PausedUntil == DateTimeOffset.MaxValue
                ? Loc.T("tray.resumeRestart")
                : Loc.T("tray.resumeUntil", controller.PausedUntil!.Value.LocalDateTime));
            resume.Click += (_, _) => controller.ResumeHistory();
            return resume;
        }
        var pause = new NativeMenuItem(Loc.T("tray.pause")) { Menu = new NativeMenu() };
        foreach (var (key, duration) in new (string, TimeSpan?)[]
                 {
                     ("tray.pause10", TimeSpan.FromMinutes(10)), ("tray.pause60", TimeSpan.FromHours(1)), ("tray.pauseRestart", null),
                 })
        {
            var item = new NativeMenuItem(Loc.T(key));
            item.Click += (_, _) => controller.PauseHistory(duration);
            pause.Menu.Items.Add(item);
        }
        return pause;
    }

    private NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        if (_updates?.Available is { } update)
        {
            // Shown only in the menu: no pop-ups for updates.
            var text = _updates.Progress is { } p ? Loc.T("update.downloading", p)
                : _updates.LastError is not null ? Loc.T("update.failed")
                : Loc.T("update.install", update.Version);
            var item = new NativeMenuItem(text) { IsEnabled = _updates.Progress is null };
            item.Click += (_, _) =>
            {
                if (_updates.LastError is not null) _updates.OpenReleasePage();
                else _ = Task.Run(InstallUpdateAsync);
            };
            menu.Items.Add(item);
            menu.Items.Add(new NativeMenuItemSeparator());
        }
        var history = new NativeMenuItem(Loc.T("tray.history"));
        history.Click += (_, _) => ShowHistory(_platform!.ForegroundWindow);
        var settings = new NativeMenuItem(Loc.T("tray.settings"));
        settings.Click += (_, _) => ShowSettings();
        var shortcuts = new NativeMenuItem(Loc.T("shortcuts.menu"));
        shortcuts.Click += (_, _) => ShowShortcuts();
        var checkUpdates = new NativeMenuItem(Loc.T("update.check"));
        checkUpdates.Click += (_, _) => ShowUpdateDialog();
        var keepAwake = new NativeMenuItem(Loc.T("tray.keepAwake"))
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _controller!.Settings.PreventScreenLock,
        };
        keepAwake.Click += (_, _) => _controller.SetPreventScreenLock(!_controller.Settings.PreventScreenLock);
        var exit = new NativeMenuItem(Loc.T("tray.exit"));
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        var capture = new NativeMenuItem(Loc.T("capture.title")) { Menu = new NativeMenu(), IsEnabled = _platform!.SupportsScreenCapture };
        foreach (var destination in new[] { CaptureDestination.Clipboard, CaptureDestination.File })
        {
            var item = new NativeMenuItem(Loc.T(destination == CaptureDestination.Clipboard ? "capture.copy" : "capture.save"));
            item.Click += (_, _) => _ = _screenCapture!.StartAsync(_controller!.Settings.ScreenshotShape, destination);
            capture.Menu.Items.Add(item);
        }
        menu.Items.Add(capture);
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(shortcuts);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(keepAwake);
        menu.Items.Add(BuildPauseItem());
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(checkUpdates);
        menu.Items.Add(exit);
        return menu;
    }

    private void ShowHistory(nint previousWindow)
    {
        if (_screenCapture?.IsActive == true) return;
        if (_controller is null) return;
        _historyWindow ??= new ClipboardWindow(_controller);
        _historyWindow.ShowFor(previousWindow);
    }

    private ShortcutsWindow? _shortcutsWindow;

    public void ShowShortcuts()
    {
        if (_controller is null) return;
        _shortcutsWindow?.Close();
        _shortcutsWindow = new ShortcutsWindow(_controller.Settings, ShowSettings, _controller.AvailableLayouts);
        _shortcutsWindow.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow.Show();
        _shortcutsWindow.Activate();
    }

    private UpdateDialog? _updateDialog;
    private PersonalWordDialog? _personalWordDialog;

    private void ShowPersonalWord(string word, string? language)
    {
        if (_controller is null) return;
        _personalWordDialog?.Close();
        _personalWordDialog = new(_controller, word, language);
        _personalWordDialog.Closed += (_, _) => _personalWordDialog = null;
        _personalWordDialog.Show(); _personalWordDialog.Activate();
    }

    /// <summary>Opens the "Check for updates" window (one at a time).</summary>
    public void ShowUpdateDialog()
        => ShowUpdateDialog(null);

    private void ShowUpdateDialog(string? error)
    {
        if (_updates is null) return;
        if (_updateDialog is { IsVisible: true })
        {
            if (error is not null) _updateDialog.ShowInstallError(error);
            _updateDialog.Activate();
            return;
        }
        _updateDialog = new UpdateDialog(_updates, InstallUpdateAsync, error);
        _updateDialog.Closed += (_, _) => _updateDialog = null;
        _updateDialog.Show();
        _updateDialog.Activate();
    }

    private void ShowSettings()
    {
        if (_screenCapture?.IsActive == true) return;
        if (_controller is null) return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_controller)
        {
            Reopen = () => Dispatcher.UIThread.Post(ShowSettings),
            CheckForUpdates = ShowUpdateDialog,
            ShowShortcuts = ShowShortcuts,
            CaptureScreenshot = (mode, destination) => _ = _screenCapture!.StartAsync(mode, destination),
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Cleanup()
    {
        _screenCapture?.Dispose();
        _ipc?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
        _platform?.Dispose();
    }
}
