using Typlo.App.Views;

namespace Typlo.App;

public partial class App
{
    private SpellingWindow? _spellingWindow;
    private void ShowSpelling(long id)
    {
        if (_controller is null) return;
        _spellingWindow?.Close();
        var window = new SpellingWindow(_controller, id); _spellingWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_spellingWindow, window)) _spellingWindow = null; };
        window.Show(); window.Activate();
    }
}
