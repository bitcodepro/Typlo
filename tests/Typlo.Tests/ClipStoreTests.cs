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
