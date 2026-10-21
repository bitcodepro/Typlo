using Typlo.Core.Storage;

namespace Typlo.App.Platform;

public static class PlatformFactory
{
    public static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new Windows.WinPlatform();
        if (OperatingSystem.IsMacOS()) return new Mac.MacPlatform();
        if (OperatingSystem.IsLinux()) return new Linux.LinuxPlatform();
        throw new PlatformNotSupportedException();
    }

    public static IKeyProtector CreateKeyProtector()
    {
        if (OperatingSystem.IsWindows()) return new DpapiKeyProtector();
        if (OperatingSystem.IsMacOS()) return new KeychainKeyProtector();
        if (OperatingSystem.IsLinux()) return new SecretServiceKeyProtector();
        return new PlainKeyProtector();
    }

    /// <summary>Short human description of where the key lives, for the settings window.</summary>
    public static string KeyStorageDescription =>
        OperatingSystem.IsWindows() ? Services.Loc.T("key.windows")
        : OperatingSystem.IsMacOS() ? Services.Loc.T("key.mac")
        : OperatingSystem.IsLinux() && SecretServiceKeyProtector.Available ? Services.Loc.T("key.secret")
        : Services.Loc.T("key.file");
}
