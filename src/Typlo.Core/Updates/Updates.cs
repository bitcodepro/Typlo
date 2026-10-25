using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Typlo.Core.Updates;

/// <summary>Semantic version (MAJOR.MINOR.PATCH[-prerelease]); build metadata after '+' is ignored.</summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemVersion>
{
    public static SemVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length != 3 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        return new SemVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), string.IsNullOrEmpty(pre) ? null : pre);
    }

    public bool IsPrerelease => Prerelease is not null;

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release is newer than any prerelease of the same version.
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi);
            var yn = int.TryParse(y[i], out var yi);
            var c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

public sealed record UpdateInfo(SemVersion Version, string PageUrl, string ArchiveName, string ArchiveUrl, string ChecksumUrl);

/// <summary>Reads a GitHub "latest release" response and picks the archive for this platform.</summary>
public static class ReleaseFeed
{
    public static string ArchiveName(SemVersion version, string rid) =>
        $"Typlo-{version}-{rid}.{(rid.StartsWith("linux-", StringComparison.Ordinal) ? "tar.gz" : "zip")}";

    /// <summary>Returns the update if the release is newer than <paramref name="current"/> and has our archive.</summary>
    public static UpdateInfo? Parse(string json, SemVersion current, string rid)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var version = SemVersion.TryParse(root.GetProperty("tag_name").GetString());
        if (version is null || !(version > current)) return null;

        var name = ArchiveName(version, rid);
        string? archive = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var assetName = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (assetName == name) archive = url;
            else if (assetName == name + ".sha256") checksum = url;
        }
        if (archive is null || checksum is null) return null;
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateInfo(version, page, name, archive, checksum);
    }
}

/// <summary>Verifies, unpacks and swaps in a downloaded release, keeping the old files until the next start.</summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".old";
    private static readonly HashSet<string> Preserved = new(StringComparer.OrdinalIgnoreCase) { "data" };

    /// <summary>Checks the file against a "sha256sum" line ("&lt;hex&gt;  &lt;name&gt;").</summary>
    public static bool VerifyChecksum(string file, string checksumText, string expectedName)
    {
        var line = checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return false;
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[1].TrimStart('*') != expectedName) return false;
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actual, parts[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unpacks a .zip or .tar.gz into an empty directory, rejecting entries that escape it.</summary>
    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries) Guard(root, entry.FullName);
            zip.ExtractToDirectory(destination); // keeps Unix permissions on macOS/Linux
            return;
        }
        using var file = File.OpenRead(archive);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            var target = Guard(root, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true); // applies the entry's mode on Unix
                    break;
                default:
                    throw new InvalidDataException($"Unsupported archive entry: {entry.Name}");
            }
        }
    }

    private static string Guard(string root, string name)
    {
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root, StringComparison.Ordinal) && target + Path.DirectorySeparatorChar != root)
            throw new InvalidDataException($"Archive entry escapes the target folder: {name}");
        return target;
    }

    /// <summary>
    /// Replaces every top-level entry of <paramref name="installRoot"/> that the new release contains.
    /// Old entries are renamed to "*.old" (a running executable can be renamed, not overwritten);
    /// on any failure everything is rolled back. The user's data folder is never touched.
    /// </summary>
    public static void Swap(string staging, string installRoot)
    {
        var done = new List<(string Target, string? Backup)>();
        // A unique suffix, so a user's own "something.old" next to the app is never overwritten or deleted.
        var suffix = $".typlo-backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            foreach (var source in Directory.EnumerateFileSystemEntries(staging))
            {
                var name = Path.GetFileName(source);
                if (Preserved.Contains(name)) continue;
                var target = Path.Combine(installRoot, name);
                string? backup = null;
                if (Exists(target))
                {
                    backup = target + suffix;
                    Move(target, backup);
                }
                done.Add((target, backup));
                Move(source, target);
            }
            // Remember exactly what we created; cleanup on the next start removes only these.
            var backups = done.Where(d => d.Backup is not null).Select(d => Path.GetFileName(d.Backup!));
            File.WriteAllLines(Path.Combine(installRoot, ManifestName), backups);
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (target, backup) = done[i];
                try
                {
                    if (Exists(target)) Delete(target);
                    if (backup is not null && Exists(backup)) Move(backup, target);
                }
                catch (IOException) { }
            }
            throw;
        }
    }

    /// <summary>Lists the backups made by the last update, one file or folder name per line.</summary>
    public const string ManifestName = ".typlo-update-backups";

    /// <summary>Top-level names of a Typlo release, for the backups of versions without a manifest (0.4.0 and older).</summary>
    private static readonly string[] ReleaseEntries =
        ["Typlo.exe", "Typlo", "Typlo.app", "LICENSE", "README.md", "THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.md", "build-info.json", "licenses", "docs"];

    /// <summary>
    /// Removes the backups of a previous update (call on start): only the entries listed in the update manifest,
    /// plus "&lt;release entry&gt;.old" left by versions before the manifest. Nothing else next to the app is touched.
    /// </summary>
    public static void CleanupOld(string installRoot)
    {
        if (!Directory.Exists(installRoot)) return;
        var manifest = Path.Combine(installRoot, ManifestName);
        var names = new List<string>();
        if (File.Exists(manifest))
            names.AddRange(File.ReadAllLines(manifest).Select(l => l.Trim())
                // Only plain names with our suffix: a damaged manifest must not point anywhere else.
                .Where(n => n.Contains(".typlo-backup-", StringComparison.Ordinal) && n == Path.GetFileName(n)));
        names.AddRange(ReleaseEntries.Select(e => e + OldSuffix));

        var allRemoved = true;
        foreach (var name in names.Distinct())
        {
            var path = Path.Combine(installRoot, name);
            if (!Exists(path)) continue;
            try { Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { allRemoved = false; }
        }
        if (allRemoved && File.Exists(manifest))
        {
            try { File.Delete(manifest); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
