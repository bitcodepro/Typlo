using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Typlo.App.Services;

internal sealed record UpdateProbeResult(string? Feed, string? Error);

/// <summary>One short-lived helper performs the automatic network check, then releases HTTP/TLS resources.</summary>
internal static class UpdateProbe
{
    internal static async Task RunAsync(UpdateService updates, string profile, CancellationToken stop, bool immediately = false)
    {
        if (!OperatingSystem.IsWindows() || !updates.CanCheck) return;
        try
        {
            if (!immediately) await Task.Delay(TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var name = "Typlo.Update." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Typlo.dll"));
            foreach (var arg in new[] { "--update-probe", "--probe-channel", name, "--profile", profile, "--parent-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Cannot start update check");
            try
            {
                await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                // A per-user pipe alone is not proof that an update feed came from our helper.
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) || pid != child.Id)
                    throw new IOException("Unexpected update helper");
                var data = await RuntimeChannel.ReadFrameAsync(server, timeout.Token).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize(data, RuntimeJson.Default.UpdateProbeResult) ?? throw new IOException("Missing update result");
                if (result.Feed is { } feed) updates.AcceptFeed(feed);
                else Log.Info("Automatic update check unavailable");
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().ConfigureAwait(false); } }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error("Automatic update check failed", e); }
    }

    internal static async Task<int> WorkerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var parent = Process.GetProcessById(int.Parse(Program.Argument("--parent-pid") ?? throw new ArgumentException("Missing parent")));
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => { try { timeout.Cancel(); } catch (ObjectDisposedException) { } };
        if (parent.HasExited) return 1;
        using var updates = new UpdateService(Program.Argument("--profile") ?? throw new ArgumentException("Missing profile"));
        UpdateProbeResult result;
        try { result = new(await updates.FetchFeedAsync(timeout.Token).ConfigureAwait(false), null); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { result = new(null, "unavailable"); }
        try
        {
            using var pipe = new NamedPipeClientStream(".", Program.Argument("--probe-channel")!, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await RuntimeChannel.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(result, RuntimeJson.Default.UpdateProbeResult), timeout.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { return 1; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
