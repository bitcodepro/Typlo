using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Typlo.Core.Updates;

namespace Typlo.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("0.1.1", "0.1.0", 1)]
    [InlineData("v1.0.0", "0.9.9", 1)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    public void Versions_Compare(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(SemVersion.TryParse(a)!.CompareTo(SemVersion.TryParse(b)!)));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.x.3")]
    public void Versions_RejectGarbage(string text) => Assert.Null(SemVersion.TryParse(text));

    private static string Release(string tag, bool prerelease = false, params string[] assets) =>
        $$"""
        {"tag_name":"{{tag}}","html_url":"https://example/r/{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
         "assets":[{{string.Join(",", assets.Select(a => $$"""{"name":"{{a}}","browser_download_url":"https://example/d/{{a}}"}"""))}}]}
        """;

    [Fact]
    public void Feed_PicksArchiveForPlatform()
    {
        var json = Release("v0.2.0", false, "Typlo-0.2.0-win-x64.zip", "Typlo-0.2.0-win-x64.zip.sha256",
            "Typlo-0.2.0-linux-x64.tar.gz", "Typlo-0.2.0-linux-x64.tar.gz.sha256");
        var win = ReleaseFeed.Parse(json, SemVersion.TryParse("0.1.0")!, "win-x64")!;
        Assert.Equal("0.2.0", win.Version.ToString());
        Assert.Equal("https://example/d/Typlo-0.2.0-win-x64.zip", win.ArchiveUrl);
        Assert.Equal("https://example/d/Typlo-0.2.0-win-x64.zip.sha256", win.ChecksumUrl);
        Assert.Equal("Typlo-0.2.0-linux-x64.tar.gz", ReleaseFeed.Parse(json, SemVersion.TryParse("0.1.0")!, "linux-x64")!.ArchiveName);
    }

    [Fact]
    public void Feed_IgnoresOlderPrereleaseOrIncomplete()
    {
        var current = SemVersion.TryParse("0.2.0")!;
        Assert.Null(ReleaseFeed.Parse(Release("v0.2.0", false, "Typlo-0.2.0-win-x64.zip", "Typlo-0.2.0-win-x64.zip.sha256"), current, "win-x64"));
        Assert.Null(ReleaseFeed.Parse(Release("v0.3.0-rc.1", true, "Typlo-0.3.0-rc.1-win-x64.zip", "Typlo-0.3.0-rc.1-win-x64.zip.sha256"), current, "win-x64"));
        Assert.Null(ReleaseFeed.Parse(Release("v0.3.0", false, "Typlo-0.3.0-win-x64.zip"), current, "win-x64")); // no checksum
        Assert.Null(ReleaseFeed.Parse(Release("v0.3.0", false, "Typlo-0.3.0-osx-arm64.zip", "Typlo-0.3.0-osx-arm64.zip.sha256"), current, "win-x64"));
    }

    [Fact]
    public void Checksum_MatchesOnlyTheRightFile()
    {
        var file = Path.Combine(_dir, "Typlo-1.0.0-win-x64.zip");
        File.WriteAllText(file, "payload");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("payload")));
        Assert.True(UpdateInstaller.VerifyChecksum(file, $"{hash}  Typlo-1.0.0-win-x64.zip\n", "Typlo-1.0.0-win-x64.zip"));
        Assert.False(UpdateInstaller.VerifyChecksum(file, $"{hash}  other.zip\n", "Typlo-1.0.0-win-x64.zip"));
        Assert.False(UpdateInstaller.VerifyChecksum(file, $"{new string('0', 64)}  Typlo-1.0.0-win-x64.zip", "Typlo-1.0.0-win-x64.zip"));
    }

    [Fact]
    public void Extract_RejectsPathTraversal()
    {
        var zip = Path.Combine(_dir, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) w.Write("x");
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(zip, Path.Combine(_dir, "out")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.txt")));
    }

    [Fact]
    public void ExtractAndSwap_ReplacesFiles_KeepsData_ThenCleansUp()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(Path.Combine(install, "data"));
        File.WriteAllText(Path.Combine(install, "Typlo.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "LICENSE"), "old license");
        File.WriteAllText(Path.Combine(install, "data", "history.db"), "user data");
        Directory.CreateDirectory(Path.Combine(install, "licenses"));
        File.WriteAllText(Path.Combine(install, "licenses", "a.txt"), "old");

        var zip = Path.Combine(_dir, "Typlo-0.2.0-win-x64.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var w = new StreamWriter(archive.CreateEntry(name).Open()); w.Write(text); }
            Add("Typlo.exe", "new exe");
            Add("LICENSE", "new license");
            Add("licenses/b.txt", "new");
            Add("data/should-not-overwrite.txt", "x");
        }
        var staging = Path.Combine(_dir, "staging");
        UpdateInstaller.Extract(zip, staging);
        UpdateInstaller.Swap(staging, install);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "Typlo.exe")));
        var backup = Assert.Single(Directory.GetFiles(install, "Typlo.exe.typlo-backup-*"));
        Assert.Equal("old exe", File.ReadAllText(backup));
        Assert.True(File.Exists(Path.Combine(install, "licenses", "b.txt")));
        Assert.False(File.Exists(Path.Combine(install, "licenses", "a.txt")));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(install, "data", "history.db")));
        Assert.False(File.Exists(Path.Combine(install, "data", "should-not-overwrite.txt")));

        UpdateInstaller.CleanupOld(install);
        Assert.Empty(Directory.GetFileSystemEntries(install, "*.typlo-backup-*"));
        Assert.False(File.Exists(Path.Combine(install, UpdateInstaller.ManifestName)));
        Assert.True(File.Exists(Path.Combine(install, "data", "history.db")));
    }

    [Fact]
    public void Cleanup_LeavesTheUsersOwnOldFilesAlone()
    {
        var install = Path.Combine(_dir, "install3");
        Directory.CreateDirectory(Path.Combine(install, "personal-notes.old"));
        File.WriteAllText(Path.Combine(install, "unrelated-report.old"), "mine");
        File.WriteAllText(Path.Combine(install, "Typlo.exe.old"), "left by v0.4.0"); // legacy backup, no manifest
        File.WriteAllText(Path.Combine(install, "LICENSE.old"), "left by v0.4.0");
        File.WriteAllLines(Path.Combine(install, UpdateInstaller.ManifestName), ["..\\outside.typlo-backup-1", "notes.txt"]);
        File.WriteAllText(Path.Combine(install, "notes.txt"), "mine");

        UpdateInstaller.CleanupOld(install);

        Assert.True(File.Exists(Path.Combine(install, "unrelated-report.old")));
        Assert.True(Directory.Exists(Path.Combine(install, "personal-notes.old")));
        Assert.True(File.Exists(Path.Combine(install, "notes.txt"))); // listed, but not a backup name
        Assert.False(File.Exists(Path.Combine(install, "Typlo.exe.old")));
        Assert.False(File.Exists(Path.Combine(install, "LICENSE.old")));
    }

    [Fact]
    public void Swap_RollsBackOnFailure()
    {
        var install = Path.Combine(_dir, "install2");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "A"), "old A");
        File.WriteAllText(Path.Combine(install, "B"), "old B");
        var staging = Path.Combine(_dir, "staging2");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "A"), "new A");
        File.WriteAllText(Path.Combine(staging, "B"), "new B");

        // Lock B so that moving it away fails half-way through the swap.
        using (new FileStream(Path.Combine(install, "B"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) return; // Unix allows renaming open files
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(staging, install));
        }
        Assert.Equal("old A", File.ReadAllText(Path.Combine(install, "A")));
        Assert.Equal("old B", File.ReadAllText(Path.Combine(install, "B")));
        Assert.Empty(Directory.GetFiles(install, "A.typlo-backup-*"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
