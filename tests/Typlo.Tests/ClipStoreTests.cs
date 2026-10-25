using System.Security.Cryptography;
using System.Text;
using Typlo.Core.Storage;

namespace Typlo.Tests;

public sealed class ClipStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private static readonly StoreLimits Roomy = new(100, 10_000_000, 5_000_000, 1_000_000);

    private string Db => Path.Combine(_dir, "history.db");
    private ClipStore Open(StoreLimits? limits = null) => new(Db, new ClipCrypto(_key), limits ?? Roomy);

    [Fact]
    public void Text_RoundTrips_AndIsEncryptedOnDisk()
    {
        const string secret = "my-very-secret-clipboard-text-12345";
        using (var store = Open())
        {
            var id = store.Add(ClipContent.FromText(secret, "notepad"))!.Value;
            Assert.Equal(secret, store.Get(id)!.Text);
            var entry = Assert.Single(store.List());
            Assert.Equal(secret, entry.PreviewText);
            Assert.Equal("notepad", entry.SourceApp);
        }
        SqliteCleanup();
        foreach (var file in Directory.GetFiles(_dir))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("notepad", Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public void Image_RoundTrips()
    {
        using var store = Open();
        var png = RandomNumberGenerator.GetBytes(5000);
        var thumb = RandomNumberGenerator.GetBytes(300);
        var id = store.Add(ClipContent.FromImage(png, thumb, 640, 480))!.Value;
        var got = store.Get(id)!;
        Assert.Equal(ClipKind.Image, got.Kind);
        Assert.Equal(png, got.ImagePng);
        var entry = Assert.Single(store.List());
        Assert.Equal(thumb, entry.Thumbnail);
        Assert.Equal((640, 480), (entry.Width, entry.Height));
    }

    [Fact]
    public void Formatting_RoundTrips_Encrypted()
    {
        const string html = "<table><tr><td><b>secret-cell</b></td></tr></table>";
        using (var store = Open())
        {
            var id = store.Add(ClipContent.FromText("secret-cell", "EXCEL", html, @"{\rtf1 secret-cell}"))!.Value;
            var got = store.Get(id)!;
            Assert.Equal(html, got.Html);
            Assert.Equal(@"{\rtf1 secret-cell}", got.Rtf);
            Assert.True(Assert.Single(store.List()).HasFormatting);
        }
        SqliteCleanup();
        foreach (var file in Directory.GetFiles(_dir))
            Assert.DoesNotContain("<table>", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [Fact]
    public void Files_RoundTrip()
    {
        using var store = Open();
        var id = store.Add(ClipContent.FromFiles([@"C:\a\report.pdf", @"C:\b\photo.jpg"], "explorer"))!.Value;
        var got = store.Get(id)!;
        Assert.Equal(ClipKind.Files, got.Kind);
        Assert.Equal([@"C:\a\report.pdf", @"C:\b\photo.jpg"], got.Files);
        var entry = Assert.Single(store.List());
        Assert.Equal(2, entry.Files.Count);
        Assert.False(entry.HasFormatting);
    }

    [Fact]
    public void OldDatabase_WithoutFormattingColumn_IsUpgraded()
    {
        Directory.CreateDirectory(_dir);
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE clips(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, created INTEGER NOT NULL,
                  last_used INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0, hash BLOB NOT NULL UNIQUE, size INTEGER NOT NULL,
                  width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, preview BLOB NOT NULL, app BLOB, payload BLOB NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        using var store = Open();
        var id = store.Add(ClipContent.FromText("after upgrade", html: "<i>x</i>"))!.Value;
        Assert.Equal("<i>x</i>", store.Get(id)!.Html);
    }

    [Fact]
    public void Duplicate_WithNewFormatting_CountsItsSize_AndEnforcesLimits()
    {
        using var store = Open(Roomy with { MaxTotalBytes = 5000 });
        store.Add(ClipContent.FromText("same"));
        store.Add(ClipContent.FromText("same", html: new string('x', 3000)));
        var (_, total) = store.Stats();
        Assert.True(total > 3000, $"size {total} ignores the formatting");

        store.Add(ClipContent.FromText("other", html: new string('y', 3000)));
        var after = store.Stats();
        Assert.True(after.TotalBytes <= 5000, $"total {after.TotalBytes} exceeds the limit");
        Assert.Equal("other", Assert.Single(store.List()).PreviewText);
    }

    [Fact]
    public void Formatting_OverTextLimit_KeepsPlainTextOnly()
    {
        using var store = Open(Roomy with { MaxTextBytes = 1000 });
        var id = store.Add(ClipContent.FromText("short text", html: new string('h', 5000)))!.Value;
        var got = store.Get(id)!;
        Assert.Equal("short text", got.Text);
        Assert.Null(got.Html);
        Assert.False(Assert.Single(store.List()).HasFormatting);
    }

    [Fact]
    public void FullTextSearch_FindsWordsBeyondThePreview()
    {
        using var store = Open();
        var longText = new string('a', 2000) + " needle-at-the-end";
        var id = store.Add(ClipContent.FromText(longText))!.Value;
        store.Add(ClipContent.FromText("something else"));
        var files = store.Add(ClipContent.FromFiles([@"C:\docs\Needle report.pdf"]))!.Value;
        Assert.DoesNotContain("needle", Assert.Single(store.List(), e => e.Id == id).PreviewText);
        Assert.Equal(new HashSet<long> { id, files }, store.SearchFullText("NEEDLE"));
        Assert.Empty(store.SearchFullText("absent"));
    }

    [Fact]
    public void FullTextSearch_SingleCjkCharacter_AndDoesNotBlockWrites()
    {
        using var store = Open(Roomy with { MaxItems = 2000, MaxTotalBytes = 100_000_000 });
        var cat = store.Add(ClipContent.FromText(new string('a', 1000) + "猫"))!.Value;
        for (var i = 0; i < 600; i++) store.Add(ClipContent.FromText($"{i} " + new string('x', 20_000)));
        Assert.Equal(new HashSet<long> { cat }, store.SearchFullText("猫"));

        // A write during a long search finishes quickly: the search holds the lock only per batch.
        var search = Task.Run(() => { for (var i = 0; i < 5; i++) store.SearchFullText("absent"); });
        Thread.Sleep(20);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        store.Add(ClipContent.FromText("written during search"));
        sw.Stop();
        search.Wait();
        Assert.True(sw.ElapsedMilliseconds < 500, $"write waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Duplicate_IsBumpedNotDuplicated()
    {
        using var store = Open();
        var a = store.Add(ClipContent.FromText("a"));
        store.Add(ClipContent.FromText("b"));
        Thread.Sleep(5);
        var again = store.Add(ClipContent.FromText("a"));
        Assert.Equal(a, again);
        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].PreviewText);
    }

    [Fact]
    public void MaxItems_EvictsOldestUnpinned()
    {
        using var store = Open(Roomy with { MaxItems = 3 });
        var first = store.Add(ClipContent.FromText("1"))!.Value;
        store.SetPinned(first, true);
        for (var i = 2; i <= 6; i++) { store.Add(ClipContent.FromText(i.ToString())); Thread.Sleep(2); }
        var texts = store.List().Select(e => e.PreviewText).ToList();
        Assert.Equal(["1", "6", "5"], texts);
    }

    [Fact]
    public void TotalSize_IsEnforced()
    {
        using var store = Open(Roomy with { MaxTotalBytes = 2500 });
        for (var i = 0; i < 5; i++) store.Add(ClipContent.FromText(new string((char)('a' + i), 1000)));
        var (count, total) = store.Stats();
        Assert.Equal(2, count);
        Assert.True(total <= 2500);
    }

    [Fact]
    public void Oversized_IsRejected()
    {
        using var store = Open(Roomy with { MaxTextBytes = 10, MaxImageBytes = 10 });
        Assert.Null(store.Add(ClipContent.FromText("01234567890")));
        Assert.Null(store.Add(ClipContent.FromImage(new byte[11], [], 1, 1)));
        Assert.Null(store.Add(ClipContent.FromText("   ")));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Persists_AcrossReopen_AndWrongKeyCannotRead()
    {
        using (var store = Open()) store.Add(ClipContent.FromText("persist me"));
        using (var store = Open()) Assert.Equal("persist me", Assert.Single(store.List()).PreviewText);
        using var other = new ClipStore(Db, new ClipCrypto(RandomNumberGenerator.GetBytes(32)), Roomy);
        Assert.Empty(other.List());
    }

    [Fact]
    public void Pin_Delete_Clear()
    {
        using var store = Open();
        var a = store.Add(ClipContent.FromText("a"))!.Value;
        var b = store.Add(ClipContent.FromText("b"))!.Value;
        store.Add(ClipContent.FromText("c"));
        store.SetPinned(a, true);
        Assert.Equal("a", store.List()[0].PreviewText);
        store.Delete(b);
        Assert.Equal(2, store.List().Count);
        store.Clear();
        Assert.Equal("a", Assert.Single(store.List()).PreviewText);
        store.Clear(includePinned: true);
        Assert.Empty(store.List());
    }

    [Fact]
    public void KeyFile_IsCreatedAndReused()
    {
        var keyPath = Path.Combine(_dir, "k.key");
        var protector = new PlainKeyProtector();
        var c1 = ClipCrypto.LoadOrCreate(keyPath, protector);
        var blob = c1.EncryptString("x");
        var c2 = ClipCrypto.LoadOrCreate(keyPath, protector);
        Assert.Equal("x", c2.DecryptString(blob));
    }

    [Fact]
    public void Crypto_DetectsTampering()
    {
        var c = new ClipCrypto(_key);
        var blob = c.EncryptString("hello");
        blob[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => c.DecryptString(blob));
    }

    [Fact]
    public void Dpapi_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiKeyProtector();
        var blob = p.Protect(_key);
        Assert.NotEqual(_key, blob);
        Assert.Equal(_key, p.Unprotect(blob));
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    public void Dispose()
    {
        SqliteCleanup();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
