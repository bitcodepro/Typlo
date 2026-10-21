using System.Diagnostics;

namespace Typlo.App.Platform.Shared;

/// <summary>Runs small helper tools (xclip, wl-paste, security, …) with binary stdin/stdout.</summary>
internal static class Proc
{
    public sealed record Result(int ExitCode, byte[] Output)
    {
        public bool Ok => ExitCode == 0;
        public string Text => System.Text.Encoding.UTF8.GetString(Output);
    }

    private static readonly Dictionary<string, bool> Exists = [];

    /// <summary>True if <paramref name="tool"/> is on PATH (cached).</summary>
    public static bool Has(string tool)
    {
        lock (Exists)
        {
            if (Exists.TryGetValue(tool, out var known)) return known;
            var found = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, tool)));
            Exists[tool] = found;
            return found;
        }
    }

    /// <param name="captureOutput">
    /// False for tools that fork a daemon holding the selection (xclip -i, wl-copy): the daemon would
    /// keep our stdout pipe open and we would wait for it.
    /// </param>
    public static Result Run(string file, IEnumerable<string> args, byte[]? stdin = null, int timeoutMs = 3000,
        bool captureOutput = true)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {file}");
        var stdout = new MemoryStream();
        var copy = captureOutput ? p.StandardOutput.BaseStream.CopyToAsync(stdout) : Task.CompletedTask;
        if (captureOutput) _ = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            return new Result(-1, []);
        }
        copy.Wait(1000);
        return new Result(p.ExitCode, stdout.ToArray());
    }

    /// <summary>Starts a long-running helper (e.g. an inhibitor) and returns it; caller kills it.</summary>
    public static Process? Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { return Process.Start(psi); }
        catch (Exception e) { Log.Error($"Cannot start {file}", e); return null; }
    }

    public static void Stop(ref Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        process?.Dispose();
        process = null;
    }
}
