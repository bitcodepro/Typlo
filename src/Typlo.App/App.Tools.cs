using Typlo.App.Views;

namespace Typlo.App;

public partial class App
{
    private ToolsWindow? _toolsWindow, _choiceWindow;
    private void ShowTools(bool choices = false)
    {
        if (_controller is null) return;
        if (choices)
        {
            if (_choiceWindow is { IsVisible: true }) return;
            _choiceWindow = new(_controller, true); _choiceWindow.Closed += (_, _) => _choiceWindow = null; _choiceWindow.Show();
            return;
        }
        if (_toolsWindow is { IsVisible: true }) { _toolsWindow.Activate(); return; }
        _toolsWindow = new(_controller, false);
        _toolsWindow.Restored += () => { _settingsWindow?.Close(); ShowSettings(); };
        _toolsWindow.Closed += (_, _) => _toolsWindow = null; _toolsWindow.Show(); _toolsWindow.Activate();
    }
}
