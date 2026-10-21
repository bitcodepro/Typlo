using System.IO.Pipes;
using System.Text;

namespace Typlo.App.Services;

/// <summary>
/// Lets a second launch pass its command line to the running instance ("Typlo --history",
/// "Typlo --convert ru"). On Wayland this is how desktop shortcuts drive the app.
/// Named pipes map to a per-user Unix domain socket on macOS/Linux.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private static string PipeName => "Typlo." + Environment.UserName;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(Action<string[]> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadToEndAsync(_cts.Token);
                    var args = line.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Log.Info("IPC command: " + string.Join(' ', args));
                    onCommand(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Log.Error("IPC server", e);
                    await Task.Delay(500);
                }
            }
        });
    }

    public static bool Send(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? ["--settings"] : args));
            client.Write(bytes);
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Dispose() => _cts.Cancel();
}
