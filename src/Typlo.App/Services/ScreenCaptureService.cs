using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Typlo.App.Platform.Windows;
using Typlo.App.Views;
using Typlo.Core.Imaging;

namespace Typlo.App.Services;

internal sealed class ScreenCaptureService(AppController controller, bool diagnostic = false) : IDisposable
{
    public bool IsActive { get; private set; }
    private CapturePreviewWindow? _preview;
    private Window? _screenHost;
    private CaptureOverlay.Session? _session;
    private bool _disposed;
    private CancellationTokenSource? _cancel;

    internal void CancelSelection() { _cancel?.Cancel(); _session?.Finish(); }

    public async Task StartAsync(CaptureShape shape, CaptureDestination destination = CaptureDestination.Clipboard)
    {
        if (_disposed || IsActive || _preview?.IsSaving == true || !controller.Platform.SupportsScreenCapture) return;
        IsActive = true;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        ImageCodec.Encoded? image = null;
        Window[] hidden = [];
        try
        {
            image = await controller.CaptureScreenshotAsync(async () =>
            {
                _preview?.Close(); _preview = null;
                hidden = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
                    .Windows.Where(w => w.IsVisible).ToArray() ?? [];
                var screens = (hidden.FirstOrDefault() ?? (_screenHost ??= new Window())).Screens.All.ToArray();
                foreach (var window in hidden) window.Hide();
                await Task.Delay(160, cancel.Token); // let the compositor remove our windows and tray menu
                Log.Info("Screen capture: reading desktop");
                var frame = await Task.Run(DesktopCapture.Grab);
                using var session = new CaptureOverlay.Session(frame, shape, diagnostic);
                if (_disposed || cancel.IsCancellationRequested) return null;
                _session = session;
                Log.Info("Screen capture: opening selection overlay");
                try { return await session.ShowAsync(screens); }
                finally { _session = null; }
            }, copyToClipboard: destination == CaptureDestination.Clipboard);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Error("Screen capture failed", e);
            if (_disposed) return;
            var close = new Button { Content = Loc.T("update.close") };
            var error = new Window
            {
                Title = Loc.T("capture.title"), Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel { Margin = new(24), Spacing = 16, Children =
                    { new TextBlock { Text = Loc.T("capture.failed"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close } },
            };
            close.Click += (_, _) => error.Close(); error.Show();
        }
        finally
        {
            foreach (var window in _disposed ? [] : hidden)
            {
                if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Contains(window) != true) continue;
                var activate = window.ShowActivated;
                window.ShowActivated = false;
                window.Show();
                window.ShowActivated = activate;
            }
            IsActive = false;
            _cancel = null;
        }
        if (_disposed || image is null) return;
        if (destination == CaptureDestination.Clipboard && !controller.Settings.ScreenshotEditorAfterCopy) return;
        _preview = new CapturePreviewWindow(image, mode => _ = StartAsync(mode, destination),
            controller.CopyScreenshotAsync, destination == CaptureDestination.File);
        _preview.Closed += (_, _) => _preview = null;
        _preview.Show(); _preview.Activate();
    }

    public void Dispose()
    {
        _disposed = true;
        _cancel?.Cancel();
        _session?.Finish();
        _preview?.Close();
        _screenHost?.Close();
    }
}
