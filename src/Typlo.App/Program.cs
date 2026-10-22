using Avalonia;
using Typlo.App.Services;

namespace Typlo.App;

internal static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        var isolated = args.Contains("--selftest") || args.Contains("--screenshots");
        using var mutex = new Mutex(true, @"Local\Typlo.SingleInstance", out var first);
        if (!first && args.Contains("--updated"))
        {
            // Started by the updater: the old version is still shutting down and holds the mutex.
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first && !isolated)
        {
            // Already running: forward commands such as --history / --convert ru to it.
            IpcServer.Send(args);
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
