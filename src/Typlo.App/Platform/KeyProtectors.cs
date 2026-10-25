using System.Runtime.Versioning;
using Typlo.App.Platform.Shared;
using Typlo.Core.Storage;

namespace Typlo.App.Platform;

/// <summary>
/// Keeps the history master key in the macOS login Keychain; the key file only holds a marker.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainKeyProtector(bool diagnostic = false) : IKeyProtector
{
    // Diagnostic runs (--selftest, --screenshots) use their own item so they can never replace the real key.
    private readonly string Service = diagnostic ? "Typlo.DiagnosticKey" : "Typlo.HistoryKey";
    private static readonly byte[] Marker = "keychain:v1"u8.ToArray();

    public byte[] Protect(byte[] key)
    {
        // -U updates an existing item. The value is passed as an argument: `security` has no stdin mode for it.
        var r = Proc.Run("/usr/bin/security",
            ["add-generic-password", "-U", "-a", Environment.UserName, "-s", Service, "-w", Convert.ToBase64String(key)]);
        if (!r.Ok) throw new InvalidOperationException("Could not store the key in the Keychain.");
        return Marker;
    }

    public byte[] Unprotect(byte[] blob)
    {
        var r = Proc.Run("/usr/bin/security", ["find-generic-password", "-a", Environment.UserName, "-s", Service, "-w"]);
        if (!r.Ok) throw new System.Security.Cryptography.CryptographicException("History key not found in the Keychain.");
        return Convert.FromBase64String(r.Text.Trim());
    }
}

/// <summary>
/// Linux: stores the key in the Secret Service (GNOME Keyring / KWallet) via <c>secret-tool</c>.
/// Without it, falls back to a user-only (0600) key file.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SecretServiceKeyProtector(bool diagnostic = false) : IKeyProtector
{
    private static readonly byte[] Marker = "secret-service:v1"u8.ToArray();
    // Diagnostic runs (--selftest, --screenshots) use their own entry so they can never replace the real key.
    private readonly string[] Attributes = ["application", "typlo", "key", diagnostic ? "diagnostic" : "history"];

    public static bool Available => Proc.Has("secret-tool");

    public byte[] Protect(byte[] key)
    {
        if (Available)
        {
            var r = Proc.Run("secret-tool", ["store", "--label=Typlo history key", .. Attributes],
                System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(key)));
            if (r.Ok) return Marker;
            Log.Error("secret-tool store failed; falling back to a 0600 key file");
        }
        return key;
    }

    public byte[] Unprotect(byte[] blob)
    {
        if (!blob.AsSpan().SequenceEqual(Marker)) return blob; // plain fallback file
        var r = Proc.Run("secret-tool", ["lookup", .. Attributes]);
        if (!r.Ok) throw new System.Security.Cryptography.CryptographicException("History key not found in the Secret Service.");
        return Convert.FromBase64String(r.Text.Trim());
    }
}
