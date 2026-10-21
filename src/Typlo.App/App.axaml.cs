using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Services;
using Typlo.App.Views;
using Typlo.Core;

namespace Typlo.App;

public partial class App : Application
{
    private AppController? _controller;
    private IPlatform? _platform;
    private ClipboardWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var paths = AppPaths.Resolve();
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
        Log.Info($"Started on {_platform.Name}, data in {paths.DataDir}");
        if (_platform.Capabilities.Notes is { } notes) Log.Info(notes);
        _ipc = new IpcServer(OnCommand);

        _controller.ShowHistoryRequested += prev => Dispatcher.UIThread.Post(() => ShowHistory(prev));
        CreateTray();
        desktop.Exit += (_, _) => Cleanup();

        if (_controller.FailedHotkeys.Count > 0 || !Program.Args.Contains("--tray") && !File.Exists(paths.SettingsFile + ".seen"))
        {
            File.WriteAllText(paths.SettingsFile + ".seen", "");
            ShowSettings();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private IpcServer? _ipc;

    /// <summary>Commands from a second instance (e.g. a desktop shortcut running "Typlo --history").</summary>
    private void OnCommand(string[] args)
    {
        if (_controller is null) return;
        if (args.Contains("--history"))
            Dispatcher.UIThread.Post(() => ShowHistory(_platform!.ForegroundWindow));
        else if (args.Contains("--settings"))
            Dispatcher.UIThread.Post(ShowSettings);
        else if (Array.IndexOf(args, "--convert") is var i and >= 0)
        {
            var target = i + 1 < args.Length ? args[i + 1] : "toggle";
            _ = _controller.ConvertSelectionAsync(Core.Layouts.KeyboardLayouts.Find(target));
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
        _controller!.SettingsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            // Rebuild everything that shows text when the interface language changes.
            if (Loc.Current != language)
            {
                language = Loc.Current;
                _historyWindow?.Close();
                _historyWindow = null;
            }
            if (_tray is not null) _tray.Menu = BuildTrayMenu();
        });
    }

    private NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        var history = new NativeMenuItem(Loc.T("tray.history"));
        history.Click += (_, _) => ShowHistory(_platform!.ForegroundWindow);
        var settings = new NativeMenuItem(Loc.T("tray.settings"));
        settings.Click += (_, _) => ShowSettings();
        var keepAwake = new NativeMenuItem(Loc.T("tray.keepAwake"))
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _controller!.Settings.PreventScreenLock,
        };
        keepAwake.Click += (_, _) => _controller.SetPreventScreenLock(!_controller.Settings.PreventScreenLock);
        var exit = new NativeMenuItem(Loc.T("tray.exit"));
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(keepAwake);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private void ShowHistory(nint previousWindow)
    {
        if (_controller is null) return;
        _historyWindow ??= new ClipboardWindow(_controller);
        _historyWindow.ShowFor(previousWindow);
    }

    private void ShowSettings()
    {
        if (_controller is null) return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_controller);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Cleanup()
    {
        _ipc?.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
        _platform?.Dispose();
    }
}
