using System.Diagnostics;
using System.Text;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Services;

/// <summary>
/// Reads text from an image, offline: the built-in Windows OCR (Windows.Media.Ocr, through Windows PowerShell's
/// WinRT bridge, in the languages installed in Windows) or <c>tesseract</c> on Linux when it is installed.
/// </summary>
public static class OcrService
{
    public static bool Available =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Proc.Has("tesseract"));

    /// <summary>Recognized text (lines separated by newlines), or null if nothing was found or OCR failed.</summary>
    public static async Task<string?> RecognizeAsync(byte[] png)
    {
        var file = Path.Combine(Path.GetTempPath(), "typlo-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(file, png);
        try
        {
            var text = OperatingSystem.IsWindows() ? await WindowsAsync(file) : OperatingSystem.IsLinux() ? Tesseract(file) : null;
            text = text?.Replace("\r\n", "\n").Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception e)
        {
            Log.Error("OCR failed", e);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private const string WindowsScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($env:TYPLO_OCR_FILE)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) { exit 2 }
        $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $result.Lines | ForEach-Object { $_.Text }
        $stream.Dispose()
        """;

    private static async Task<string?> WindowsAsync(string file)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", WindowsScript },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["TYPLO_OCR_FILE"] = file; // never interpolate paths into the script
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { p.Kill(true); return null; }
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) Log.Error($"Windows OCR exit {p.ExitCode}: {(await error).Split('\n').FirstOrDefault()}");
        return p.ExitCode == 0 ? await output : null;
    }

    private static string? Tesseract(string file)
    {
        // Use the languages Typlo works with when their data is installed.
        var installed = Proc.Run("tesseract", ["--list-langs"]).Text;
        var langs = string.Join('+', new[] { "eng", "rus", "ukr" }.Where(l => installed.Contains(l)));
        var args = new List<string> { file, "-" };
        if (langs.Length > 0) args.AddRange(["-l", langs]);
        var r = Proc.Run("tesseract", args, timeoutMs: 30_000);
        return r.Ok ? r.Text : null;
    }
}
