using System.Security.Cryptography;
using System.Text;

namespace Typlo.Core.Storage;

/// <summary>Protects the master key at rest (DPAPI on Windows).</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] key);
    byte[] Unprotect(byte[] blob);
}

/// <summary>Windows DPAPI, CurrentUser scope: the key file is useless for other users and machines.</summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = "Typlo.ClipboardHistory.v1"u8.ToArray();

    public byte[] Protect(byte[] key) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();

    public byte[] Unprotect(byte[] blob) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();
}

/// <summary>
/// Placeholder for platforms without a wired-up keychain yet: the key is stored as is,
/// so only file permissions protect it. Replace with Keychain / libsecret per platform.
/// </summary>
public sealed class PlainKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] key) => key;
    public byte[] Unprotect(byte[] blob) => blob;
}

/// <summary>AES-256-GCM for payloads plus a keyed hash for de-duplication without leaking content.</summary>
public sealed class ClipCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _encKey;
    private readonly byte[] _macKey;

    public ClipCrypto(byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Master key must be 32 bytes.");
        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "enc"u8.ToArray());
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "mac"u8.ToArray());
    }

    public static ClipCrypto LoadOrCreate(string keyPath, IKeyProtector protector)
    {
        if (File.Exists(keyPath))
            return new ClipCrypto(protector.Unprotect(File.ReadAllBytes(keyPath)));
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, protector.Protect(key));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new ClipCrypto(key);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var result = new byte[NonceSize + TagSize + plain.Length];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(_encKey, TagSize);
        gcm.Encrypt(nonce, plain, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize));
        return result;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("Blob too short.");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        using var gcm = new AesGcm(_encKey, TagSize);
        gcm.Decrypt(blob[..NonceSize], blob[(NonceSize + TagSize)..], blob.Slice(NonceSize, TagSize), plain);
        return plain;
    }

    public byte[] EncryptString(string s) => Encrypt(Encoding.UTF8.GetBytes(s));
    public string DecryptString(ReadOnlySpan<byte> blob) => Encoding.UTF8.GetString(Decrypt(blob));

    public byte[] Hash(ClipKind kind, ReadOnlySpan<byte> content)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _macKey);
        h.AppendData([(byte)kind]);
        h.AppendData(content);
        return h.GetHashAndReset();
    }
}
