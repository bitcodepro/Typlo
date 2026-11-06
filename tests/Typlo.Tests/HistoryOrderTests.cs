using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Settings;
using Typlo.Core.Storage;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    private void SetHistoryOrder(bool move)
    {
        var settings = _controller.Settings.Clone(); settings.HistoryMoveUsedToTop = move;
        _controller.ApplySettings(settings);
    }
    private (long Selected, long Newest) SeedHistory(ClipContent content)
    {
        var start = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var selected = _controller.Store.Add(content, start)!.Value;
        var newest = _controller.Store.Add(ClipContent.FromText("newest history item"), start.AddSeconds(1))!.Value;
        return (selected, newest);
    }
    [Theory]
    [InlineData(true, "text")]
    [InlineData(false, "text")]
    [InlineData(true, "rich")]
    [InlineData(false, "rich")]
    [InlineData(true, "files")]
    [InlineData(false, "files")]
    [InlineData(true, "image")]
    [InlineData(false, "image")]
    public async Task HistoryPasteHonorsOrderForEveryContentType(bool move, string kind)
    {
        SetHistoryOrder(move);
        ClipContent content;
        if (kind == "image")
        {
            var image = ImageCodec.FromBgra([0, 0, 255, 255], 1, 1);
            content = ClipContent.FromImage(image.Png, image.Thumbnail, 1, 1);
        }
        else content = kind switch
        {
            "files" => ClipContent.FromFiles([@"C:\test.txt"]),
            "rich" => ClipContent.FromText("older item", html: "<b>older item</b>"),
            _ => ClipContent.FromText("older item"),
        };
        var (selected, newest) = SeedHistory(content);
        var before = _controller.Store.List(includeThumbnails: false).Select(e => (e.Id, e.LastUsed)).ToArray();
        var changes = 0; _controller.Store.Changed += () => changes++;
        await _controller.PasteAsync(selected, 1);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Equal(move ? selected : newest, _controller.Store.List(includeThumbnails: false)[0].Id);
        Assert.Equal(move ? 1 : 0, changes);
        if (!move) Assert.Equal(before, _controller.Store.List(includeThumbnails: false).Select(e => (e.Id, e.LastUsed)).ToArray());
        Assert.Equal(2, _controller.Store.Stats().Count);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RemoteHistoryUseHonorsResidentOrderForPasteAndCopy(bool move, bool paste)
    {
        SetHistoryOrder(move);
        var (selected, newest) = SeedHistory(ClipContent.FromText("older item"));
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        using var server = RemoteServer(name);
        var passive = new FakePlatform();
        using var ui = AppController.CreateRemote(passive, new AppPaths(_dir), new PlainKeyProtector(), new RuntimeClient(name));
        await ui.PasteAsync(selected, 1, paste);
        Assert.Equal(move ? selected : newest, _controller.Store.List()[0].Id);
        Assert.Equal(paste ? 1 : 0, _platform.PasteCount);
        Assert.Equal(0, passive.PasteCount);
        Assert.Equal("older item", _platform.ClipboardText);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlainHistoryPasteAndPinnedGroupHonorTheOrderSetting(bool move)
    {
        SetHistoryOrder(move);
        var (selected, newest) = SeedHistory(ClipContent.FromText("older item", html: "<b>older item</b>"));
        var pinned = _controller.Store.Add(ClipContent.FromText("pinned"), DateTimeOffset.Parse("2019-01-01T00:00:00Z"))!.Value;
        _controller.Store.SetPinned(pinned, true);
        await _controller.PasteAsync(selected, 1, plainText: true);
        Assert.Equal("older item", _platform.Document.Text);
        Assert.Equal(new[] { pinned, move ? selected : newest, move ? newest : selected }, _controller.Store.List().Select(e => e.Id));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledHistoryPasteDoesNotPromoteTheItem(bool clipboardChanged)
    {
        SetHistoryOrder(true);
        var (selected, newest) = SeedHistory(ClipContent.FromText("older item"));
        _platform.BeforeModifiers = () =>
        {
            if (clipboardChanged) _platform.ExternalCopy("external copy"); else _platform.Window = 2;
            return Task.CompletedTask;
        };
        await _controller.PasteAsync(selected, 1);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(newest, _controller.Store.List()[0].Id);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryOrderSurvivesSettingsAndFullBackup(bool move)
    {
        Assert.True(new AppSettings().HistoryMoveUsedToTop);
        SetHistoryOrder(move);
        Assert.Equal(move, _controller.Settings.Clone().HistoryMoveUsedToTop);
        var store = new SettingsStore(new AppPaths(_dir).SettingsFile);
        Assert.Equal(move, store.Load().HistoryMoveUsedToTop);
        var export = Path.Combine(_dir, "settings-export.json"); store.Export(_controller.Settings, export);
        Assert.Equal(move, store.Import(export).HistoryMoveUsedToTop);
        var backup = Path.Combine(_dir, "history-order.typlo");
        await _controller.ExportBackupAsync(backup); var info = await _controller.PreviewBackupAsync(backup);
        SetHistoryOrder(!move); await _controller.RestoreBackupAsync(backup, info.Hash);
        Assert.Equal(move, _controller.PublishedSettings.HistoryMoveUsedToTop);
    }
    [Fact]
    public void OlderSettingsDefaultToPromotingUsedItems()
    {
        var file = Path.Combine(_dir, "legacy-settings.json");
        File.WriteAllText(file, "{\"HistoryEnabled\":true}");
        Assert.True(new SettingsStore(file).Load().HistoryMoveUsedToTop);
    }
    [Fact]
    public void NewCopyStillPromotesAnExistingItemInKeepPositionMode()
    {
        SetHistoryOrder(false);
        var content = ClipContent.FromText("older item");
        var (selected, _) = SeedHistory(content);
        Assert.Equal(selected, _controller.Store.Add(content));
        Assert.Equal(selected, _controller.Store.List()[0].Id);
        Assert.Equal(2, _controller.Store.Stats().Count);
    }
}
