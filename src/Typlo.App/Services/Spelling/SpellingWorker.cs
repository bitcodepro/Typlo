using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Typlo.App.Services;

internal static class SpellingWorker
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
            var input = new char[500_000]; var count = 0;
            while (count < input.Length)
            {
                var n = await Console.In.ReadAsync(input.AsMemory(count)); if (n == 0) break; count += n;
            }
            if (count == input.Length) throw new InvalidDataException("Spelling input too large");
            var request = JsonSerializer.Deserialize(input.AsSpan(0, count), SpellingJson.Default.SpellingRequest) ?? throw new InvalidDataException();
            var result = new SpellingEngine(Path.Combine(AppContext.BaseDirectory, "spelling")).Check(request);
            await Console.Out.WriteAsync(JsonSerializer.Serialize(result, SpellingJson.Default.SpellingResult));
            return 0;
        }
        catch { await Console.Error.WriteAsync("Spelling worker could not load or check the dictionaries."); return 1; }
    }
    internal static async Task<SpellingResult> CheckAsync(SpellingRequest request, CancellationToken cancellationToken, string? executable = null, Action<int>? started = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken.ThrowIfCancellationRequested();
        var path = executable ?? Environment.ProcessPath ?? throw new IOException("Cannot resolve application path");
        var dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? "dotnet" : path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        if (dll) start.ArgumentList.Add(path);
        else if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Typlo.dll"));
        start.ArgumentList.Add("--spelling-worker");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start spelling worker");
        using var stop = timeout.Token.Register(() => Stop(process));
        var output = ReadBoundedAsync(process.StandardOutput, 3_000_000, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
        try
        {
            started?.Invoke(process.Id);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, SpellingJson.Default.SpellingRequest).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output; await error;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("Spelling worker failed");
            return JsonSerializer.Deserialize(result, SpellingJson.Default.SpellingResult) ?? throw new InvalidDataException("Invalid spelling response");
        }
        catch (Exception) when (timeout.IsCancellationRequested) { throw new OperationCanceledException(timeout.Token); }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, error); } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }
    private static void Stop(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { if (result.Length > limit - count) throw new InvalidDataException("Spelling response too large"); result.Append(buffer, 0, count); }
        return result.ToString();
    }
}
