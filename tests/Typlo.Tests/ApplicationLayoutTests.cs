using Typlo.Core;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    private void SetApplicationRules(params ApplicationLayoutRule[] rules)
    {
        _controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
        var settings = _controller.Settings.Clone();
        settings.Layouts = ["en", "ru", "uk"];
        settings.ApplicationLayouts = rules.ToList();
        _controller.ApplySettings(settings);
    }

    [Fact]
    public async Task ApplicationCycleUsesTheBoundAppAndLeavesGlobalCycleAvailable()
    {
        _platform.ApplicationProcess = window => window == 1 ? "ms-teams" : "other";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "MS-TEAMS.EXE", Layouts = ["en", "uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Type("ss");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("іі", _platform.Document.Text);
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ss", _platform.Document.Text);
        _platform.Window = 2;
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Type("ss");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ыы", _platform.Document.Text);
    }

    [Fact]
    public async Task ApplicationCycleStaysBoundWhenNotificationTakesForegroundDuringNativeWrite()
    {
        EnableDirect(() => _platform.Window = 2);
        _platform.ApplicationProcess = window => window == 1 ? "editor" : "notification";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["en", "uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Type("ss"); await _controller.ConvertSelectionAsync(null);
        Assert.Equal("іі", _platform.DocumentFor(1).Text);
        Assert.Equal("", _platform.DocumentFor(2).Text);
    }

    [Fact]
    public async Task ApplicationCycleWorksWithOneGlobalLayoutAndDoesNotEnableGlobalConversionElsewhere()
    {
        _platform.ApplicationProcess = window => window == 1 ? "editor" : "other";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["en", "uk"] });
        var settings = _controller.Settings.Clone(); settings.Layouts = ["en"]; _controller.ApplySettings(settings);
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Type("ss"); await _controller.ConvertSelectionAsync(null);
        Assert.Equal("іі", _platform.Document.Text);
        _platform.Window = 2;
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Type("ss"); var copies = _platform.SuspendCalls;
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ss", _platform.Document.Text);
        Assert.Equal(copies, _platform.SuspendCalls);
    }

    [Fact]
    public async Task SingleLayoutActivatesOnEntryWithoutEditingAndDoesNotLockLaterChoices()
    {
        _platform.ApplicationProcess = window => window == 2 ? "editor" : "other";
        _platform.ActiveLayoutId = "en";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        _platform.Window = 2;
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Equal("uk", _platform.ActiveLayoutId);
        Assert.Single(_platform.LayoutSwitches);
        Assert.Equal(0, _platform.PasteCount);
        _platform.ActiveLayoutId = "en"; // A user's manual layout change while staying in the app.
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Equal("en", _platform.ActiveLayoutId);
        _platform.Window = 1; _platform.Window = 2;
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Equal("uk", _platform.ActiveLayoutId);
        Assert.Equal(2, _platform.LayoutSwitches.Count);
    }

    [Fact]
    public async Task SingleLayoutActivationIsIndependentOfConvertSwitchSetting()
    {
        _platform.ApplicationProcess = _ => "editor";
        var settings = _controller.Settings.Clone(); settings.SwitchSystemLayout = false; _controller.ApplySettings(settings);
        _platform.ActiveLayoutId = "en";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["ru"] });
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Equal("ru", _platform.ActiveLayoutId);
        Assert.False(_controller.Settings.SwitchSystemLayout);
    }

    [Fact]
    public async Task UnavailableMemberDoesNotTurnTwoLayoutRuleIntoAutomaticSingleLayout()
    {
        _platform.ApplicationProcess = _ => "editor";
        _platform.ActiveLayoutId = "en";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk", "missing-native-layout"] });
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Empty(_platform.LayoutSwitches);
        _platform.Type("ss"); await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ыы", _platform.Document.Text);
    }

    [Fact]
    public async Task DisabledOrUnconfiguredRulesDoNotResolveApplications()
    {
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk"], Enabled = false });
        _platform.Window = 2;
        _platform.Type("ghb"); await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(0, _platform.ApplicationLookups);
        Assert.Equal("при", _platform.Document.Text);
    }

    [Fact]
    public async Task PendingEntryCannotSwitchAnAppAfterNewTypingOrASecondFocusChange()
    {
        _platform.ApplicationProcess = window => window == 2 ? "editor" : "other";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.BeforeModifiers = () => release.Task;
        _platform.Type("ghb");
        var converting = _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await _platform.ModifiersEntered.Task;
        _platform.Window = 2;
        _platform.Type("x");
        _platform.Window = 3;
        release.TrySetResult();
        await converting;
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Empty(_platform.LayoutSwitches);
        Assert.Equal("x", _platform.DocumentFor(2).Text);
    }

    [Fact]
    public async Task DisposingControllerCancelsAnEntryWaitingForTheOperationGate()
    {
        _platform.ApplicationProcess = window => window == 2 ? "editor" : "other";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.True(await _controller.BeginExternalCaptureAsync());
        _platform.Window = 2;
        _controller.Dispose();
        await _controller.ApplicationLayoutsIdleAsync();
        Assert.Empty(_platform.LayoutSwitches);
    }

    [Fact]
    public async Task NotificationReturnDuringConversionDoesNotOverrideTheManualTarget()
    {
        _platform.ApplicationProcess = window => window == 1 ? "editor" : "other";
        _platform.ActiveLayoutId = "en";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["en"] });
        await _controller.ApplicationLayoutsIdleAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.BeforeModifiers = () => release.Task;
        _platform.Type("ghb");
        var converting = _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await _platform.ModifiersEntered.Task;
        _platform.Window = 2; _platform.Window = 1;
        release.TrySetResult();
        await converting; await _controller.ApplicationLayoutsIdleAsync();
        Assert.Equal("при", _platform.Document.Text);
        Assert.Equal("ru", _platform.ActiveLayoutId);
        Assert.Single(_platform.LayoutSwitches);
    }

    [Fact]
    public async Task ManualConversionIsNotDroppedWhileAutomaticLayoutReadOwnsTheGate()
    {
        _platform.ApplicationProcess = window => window == 2 ? "editor" : "other";
        _platform.ActiveLayoutId = "en";
        SetApplicationRules(new ApplicationLayoutRule { ProcessName = "editor", Layouts = ["uk"] });
        await _controller.ApplicationLayoutsIdleAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        _platform.BeforeLayoutRead = window =>
        {
            if (window != 2 || Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
        };
        try
        {
            _platform.Window = 2;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            _platform.Type("ss");
            var converting = _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
            Assert.False(converting.IsCompleted);
            release.TrySetResult();
            await converting; await _controller.ApplicationLayoutsIdleAsync();
            Assert.Equal("ыы", _platform.Document.Text);
            Assert.Equal("ru", _platform.ActiveLayoutId);
        }
        finally { release.TrySetResult(); }
    }
}

public class ApplicationLayoutSettingsTests
{
    [Fact]
    public void RulesRoundTripCloneNormalizeAndValidateWithoutChangingGlobalOrder()
    {
        var settings = new AppSettings { Layouts = ["en", "ru"], ApplicationLayouts =
            [new() { ProcessName = @"C:\Program Files\Teams\MS-TEAMS.EXE", Layouts = ["uk", "en", "uk"] }] };
        settings.Normalize();
        Assert.Equal("MS-TEAMS", settings.ApplicationLayouts[0].ProcessName);
        Assert.Equal(["uk", "en"], settings.ApplicationLayouts[0].Layouts);
        var clone = settings.Clone(); clone.ApplicationLayouts[0].Layouts.Add("ru");
        Assert.Equal(2, settings.ApplicationLayouts[0].Layouts.Count);
        Assert.Equal(["en", "ru"], settings.Layouts);
        settings.ApplicationLayouts.Add(new() { ProcessName = "ms-teams", Layouts = ["en"] });
        Assert.Contains(settings.Validate(), problem => problem.Kind == SettingsProblemKind.DuplicateAppProcess);
        settings.ApplicationLayouts[1].Enabled = false;
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void CatalogMigratesRuleAliasesAndPreservesUnavailableIds()
    {
        var native = new KeyboardLayout("native:en", "English", 0, KeyboardLayouts.English.Normal, KeyboardLayouts.English.Shifted, "en");
        var settings = new AppSettings { ApplicationLayouts = [new() { ProcessName = "editor", Layouts = ["en", "missing"] }] };
        new LayoutCatalog([native]).Synchronize(settings);
        Assert.Equal(["native:en", "missing"], settings.ApplicationLayouts[0].Layouts);
    }

    [Fact]
    public void ApplicationRulesSurviveSettingsExportImport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "typlo-app-rules-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new AppSettings { ApplicationLayouts = [new() { ProcessName = "editor.exe", Layouts = ["uk", "en"] }] };
            var store = new SettingsStore(file);
            store.Export(settings, file);
            var imported = store.Import(file);
            Assert.Equal("editor", Assert.Single(imported.ApplicationLayouts).ProcessName);
            Assert.Equal(["uk", "en"], imported.ApplicationLayouts[0].Layouts);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
