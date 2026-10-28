using System.Diagnostics;
using System.Text;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Services;

/// <summary>Offline OCR using Windows OCR or an installed Linux tesseract. Images stay in memory.</summary>
public static class OcrService
{
    public static bool Available =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Proc.Has("tesseract"));

    public static async Task<string?> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? text = null;
            if (OperatingSystem.IsWindows())
                text = await RunAsync(new("powershell.exe")
                {
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", WindowsScript },
                }, png, cancellationToken).ConfigureAwait(false);
            else if (OperatingSystem.IsLinux())
            {
                var installed = await RunAsync(new("tesseract") { ArgumentList = { "--list-langs" } }, [], cancellationToken).ConfigureAwait(false) ?? "";
                var langs = string.Join('+', new[] { "eng", "rus", "ukr" }.Where(l => installed.Split('\n').Any(s => s.Trim() == l)));
                var start = new ProcessStartInfo("tesseract") { ArgumentList = { "stdin", "stdout" } };
                if (langs.Length > 0) { start.ArgumentList.Add("-l"); start.ArgumentList.Add(langs); }
                text = await RunAsync(start, png, cancellationToken).ConfigureAwait(false);
            }
            text = text?.Replace("\r\n", "\n").Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { Log.Error("OCR failed", e); return null; }
    }

    private const string WindowsScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
        $memory = [IO.MemoryStream]::new()
        [Console]::OpenStandardInput().CopyTo($memory)
        $memory.Position = 0
        $stream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
        try {
            $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            try {
                $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
                if ($null -eq $engine) { exit 2 }
                $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $result.Lines | ForEach-Object { $_.Text }
            } finally { if ($null -ne $bitmap) { $bitmap.Dispose() } }
        } finally { $stream.Dispose(); $memory.Dispose() }
        """;

    // One timeout covers stdin, recognition and output. Cancellation kills and reaps the owned helper
    // before releasing the UI lease; no plaintext image is ever written to a temporary file.
    internal static async Task<string?> RunAsync(ProcessStartInfo start, byte[] input, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new IOException("Cannot start OCR helper");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) Log.Error($"OCR exit {process.ExitCode}: {(await error.ConfigureAwait(false)).Split('\n').FirstOrDefault()}");
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
