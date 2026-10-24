using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Typlo.App.Services;

namespace Typlo.App.Views;

/// <summary>
/// "Check for updates" window: checks GitHub, then either says the app is up to date or offers
/// "Update now" / "Later". Opened only when the user asks for it.
/// </summary>
public sealed class UpdateDialog : Window
{
    private readonly UpdateService _updates;
    private readonly Func<Task> _install;
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, IsVisible = false };
    private readonly Button _primary = new() { Classes = { "accent" }, IsVisible = false };
    private readonly Button _secondary = new();

    public UpdateDialog(UpdateService updates, Func<Task> install)
    {
        _updates = updates;
        _install = install;
        Title = Loc.T("update.dialogTitle");
        Icon = AppIcon.Create();
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                _title,
                _details,
                _progress,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 0), Children = { _secondary, _primary },
                },
            },
        };
        _secondary.Click += (_, _) => Close();
        _primary.Click += async (_, _) => await OnPrimaryAsync();
        _updates.Changed += OnUpdatesChanged;
        Closed += (_, _) => _updates.Changed -= OnUpdatesChanged;
        Opened += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        _title.Text = Loc.T("update.checking");
        _details.Text = Loc.T("update.current", UpdateService.CurrentVersion);
        _progress.IsVisible = true;
        _primary.IsVisible = false;
        _secondary.Content = Loc.T("update.close");
        if (!_updates.CanCheck)
        {
            ShowResult(Loc.T("update.devBuild"), Loc.T("update.current", UpdateService.CurrentVersion));
            return;
        }
        var info = await _updates.CheckAsync();
        if (_updates.LastCheckError is not null)
        {
            ShowResult(Loc.T("update.checkFailed"), _updates.LastCheckError);
            _primary.Content = Loc.T("update.retry");
            _primary.IsVisible = true;
            return;
        }
        if (info is null)
        {
            ShowResult(Loc.T("update.upToDate"), Loc.T("update.current", UpdateService.CurrentVersion));
            return;
        }
        ShowResult(Loc.T("update.available", info.Version), Loc.T("update.availableDetails", UpdateService.CurrentVersion, info.Version));
        _primary.Content = Loc.T("update.installNow");
        _primary.IsVisible = true;
        _secondary.Content = Loc.T("update.later");
    }

    private void ShowResult(string title, string details)
    {
        _progress.IsVisible = false;
        _title.Text = title;
        _details.Text = details;
    }

    private async Task OnPrimaryAsync()
    {
        if (_updates.Available is null || _updates.LastCheckError is not null)
        {
            await CheckAsync(); // "Try again"
            return;
        }
        _primary.IsEnabled = false;
        _secondary.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.IsIndeterminate = false;
        _title.Text = Loc.T("update.downloading", 0);
        await _install();
        // Still here: the install failed (on success the app restarts).
        _primary.IsEnabled = true;
        _secondary.IsEnabled = true;
        _progress.IsVisible = false;
        if (_updates.LastError is { } error)
        {
            ShowResult(Loc.T("update.failed"), error);
            _primary.Content = Loc.T("update.openPage");
            _primary.Click -= async (_, _) => await OnPrimaryAsync();
            _primary.IsVisible = false;
            var open = new Button { Content = Loc.T("update.openPage") };
            open.Click += (_, _) => _updates.OpenReleasePage();
            ((StackPanel)((StackPanel)Content!).Children[^1]).Children.Insert(0, open);
        }
    }

    private void OnUpdatesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_updates.Progress is { } p && !_progress.IsIndeterminate)
        {
            _progress.Value = p;
            _title.Text = Loc.T("update.downloading", p);
        }
    });
}
