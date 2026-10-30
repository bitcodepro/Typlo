using Avalonia;
using Typlo.App.Services;
using Typlo.Core;

namespace Typlo.App;

internal static class Program
{
    public static string[] Args { get; private set; } = [];

    internal static bool IsRemoteUi => Args.Contains("--ui");
    internal static string? Argument(string name) => Array.IndexOf(Args, name) is var i and >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    internal static AppPaths DataPaths => (IsRemoteUi || Args.Contains("--background-probe")) && Argument("--profile") is { } path
        ? new AppPaths(Path.GetFullPath(path)) : AppPaths.Resolve();

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        if (OperatingSystem.IsWindows() && args is ["--selftest-text-copy", var textFile])
        {
            try
            {
                using var clipboard = new Platform.Windows.WinPlatform(monitorClipboard: false);
                if (Console.ReadLine() != "copy") return 1;
                clipboard.SetText(File.ReadAllText(textFile));
                Console.WriteLine("COPIED"); Console.Out.Flush();
                return Console.ReadLine() == "exit" ? 0 : 1;
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (OperatingSystem.IsWindows() && args is ["--selftest-html-copy", var htmlFile, var plain])
        {
            // Isolated self-test producer: publish materialized formats in one native clipboard
            // transaction instead of racing OLE delayed rendering against clipboard monitoring.
            try
            {
                using var clipboard = new Platform.Windows.WinPlatform(monitorClipboard: false);
                clipboard.SetRichText(plain, File.ReadAllText(htmlFile), null);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (args.Contains("--update-probe"))
        {
            try { return UpdateProbe.WorkerAsync().GetAwaiter().GetResult(); }
            catch (Exception e) { Log.Error("Update helper failed", e); return 1; }
        }
        if (IsRemoteUi) return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        var probe = args.Contains("--background-probe");
        var isolated = args.Contains("--selftest") || args.Contains("--screenshots") || probe;
        using var mutex = new Mutex(true, probe ? @"Local\Typlo.Probe." + Guid.NewGuid().ToString("N") : @"Local\Typlo.SingleInstance", out var first);
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
        if (OperatingSystem.IsWindows() && (probe || !isolated)) return BackgroundHost.Run(args, DataPaths, probe);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
