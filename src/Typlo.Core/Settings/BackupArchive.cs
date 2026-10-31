using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Typlo.Core.Languages;

namespace Typlo.Core.Settings;

public sealed record BackupInfo(string Hash, int Words, int Exclusions, int Layouts, int Snippets, int Profiles);

/// <summary>Fixed-entry archive; bounded extraction, complete validation before any live data changes.</summary>
public static class BackupArchive
{
    private const long MaxBytes = 64 * 1024 * 1024;
    private const string Format = "typlo-backup-v1";
    public sealed class Staged : IDisposable
    {
        internal Staged(string directory, AppSettings settings, BackupInfo info) { Directory = directory; Settings = settings; Info = info; }
        public string Directory { get; }
        public string Database => Path.Combine(Directory, "personal.sqlite3");
        public AppSettings Settings { get; }
        public BackupInfo Info { get; }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    public static Staged Read(string file, string stagingParent)
    {
        if (new FileInfo(file).Length > MaxBytes) throw new InvalidDataException("Backup exceeds 64 MiB.");
        System.IO.Directory.CreateDirectory(stagingParent);
        var stage = Path.Combine(stagingParent, ".backup-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(stage);
        try
        {
            using var input = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(input)); input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count != 3 || zip.Entries.Select(e => e.FullName).Distinct().Count() != 3 ||
                zip.Entries.Any(e => e.FullName is not ("format.txt" or "settings.json" or "personal.sqlite3"))) throw new InvalidDataException("Not a Typlo backup.");
            foreach (var entry in zip.Entries)
            {
                var limit = entry.FullName == "personal.sqlite3" ? MaxBytes : entry.FullName == "settings.json" ? 1024 * 1024 : 64;
                if (entry.Length > limit) throw new InvalidDataException("Backup entry exceeds its limit.");
                using var source = entry.Open(); using var target = File.Create(Path.Combine(stage, entry.FullName));
                var buffer = new byte[16 * 1024]; long copied = 0; int read;
                while ((read = source.Read(buffer)) != 0) { copied += read; if (copied > limit) throw new InvalidDataException("Oversized backup entry."); target.Write(buffer, 0, read); }
            }
            if (File.ReadAllText(Path.Combine(stage, "format.txt")) != Format) throw new InvalidDataException("Unsupported backup version.");
            var settings = new SettingsStore(Path.Combine(stage, "settings.json")).Import(Path.Combine(stage, "settings.json"));
            var count = PersonalLexicon.ValidateSnapshot(Path.Combine(stage, "personal.sqlite3"));
            return new(stage, settings, new(hash, count.Words, count.Exclusions, settings.Layouts.Count, settings.Snippets.Count, settings.ApplicationLayouts.Count));
        }
        catch { System.IO.Directory.Delete(stage, true); throw; }
    }

    public static void Write(string file, AppSettings settings, PersonalLexicon words)
    {
        var target = Path.GetFullPath(file);
        if (!target.EndsWith(".typlo", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a .typlo backup file.");
        var directory = Path.Combine(Path.GetDirectoryName(target)!, ".backup-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            words.SaveSnapshot(Path.Combine(directory, "personal.sqlite3"));
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
            File.WriteAllText(Path.Combine(directory, "format.txt"), Format);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var name in new[] { "format.txt", "settings.json", "personal.sqlite3" }) archive.CreateEntryFromFile(Path.Combine(directory, name), name, CompressionLevel.Fastest);
            using var validated = Read(temporary, directory);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); System.IO.Directory.Delete(directory, true); }
    }

    public static string RecoveryDirectory(string data) => Path.Combine(data, ".restore-pending");
    /// <summary>Both commit and successful rollback retire the snapshot before normal writes resume.</summary>
    public static void CompleteRecovery(string recovery)
    {
        using var marker = new FileStream(Path.Combine(recovery, "committed"), FileMode.Create, FileAccess.Write, FileShare.None);
        marker.WriteByte(1); marker.Flush(flushToDisk: true);
    }
    public static void Recover(string data)
    {
        var recovery = RecoveryDirectory(data); if (!System.IO.Directory.Exists(recovery)) return;
        if (File.Exists(Path.Combine(recovery, "committed"))) { System.IO.Directory.Delete(recovery, true); return; }
        // Files originate from a durable local snapshot taken before restore, not archive-provided paths.
        File.Copy(Path.Combine(recovery, "personal.sqlite3"), Path.Combine(data, "personal-words.sqlite3"), true);
        File.Copy(Path.Combine(recovery, "settings.json"), Path.Combine(data, "settings.json"), true);
        System.IO.Directory.Delete(recovery, true);
    }
}
