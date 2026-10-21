namespace Typlo.Core;

/// <summary>
/// Data lives next to the executable (portable install). If that folder is not writable
/// (e.g. Program Files), it falls back to the per-user application data folder.
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string HistoryDb => Path.Combine(DataDir, "history.db");
    public string KeyFile => Path.Combine(DataDir, "history.key");
    public string LogFile => Path.Combine(DataDir, "typlo.log");

    public AppPaths(string dataDir) => DataDir = dataDir;

    public static AppPaths Resolve()
    {
        // Inside a macOS .app bundle the folder must stay untouched (signing, App Translocation).
        var inBundle = AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal);
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (!inBundle && IsWritable(portable)) return new AppPaths(portable);
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typlo");
        Directory.CreateDirectory(roaming);
        return new AppPaths(roaming);
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
