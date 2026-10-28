using System.Globalization;

namespace Typlo.App.Platform.Windows;

// Explicit integration-test sink only. Never include text, window titles or native pointers.
internal readonly record struct NativeEditDiagnostic(string Operation, string Outcome, double ElapsedMs,
    double CallElapsedMs = 0, uint Message = 0, nuint Result = 0, int NativeError = 0)
{
    internal string Summary => string.Create(CultureInfo.InvariantCulture,
        $"operation={Operation} outcome={Outcome} elapsed_ms={ElapsedMs:F2} call_ms={CallElapsedMs:F2} message=0x{Message:X} result={Result} native_error={NativeError}");
}
