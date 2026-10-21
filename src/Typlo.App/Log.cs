namespace Typlo.App;

/// <summary>Tiny append-only log next to the data (never contains clipboard content).</summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    public static string? FilePath { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        if (FilePath is null) return;
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.MoveTo(FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
