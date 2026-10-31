using Typlo.App.Services;
using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData("ghb", AutoCorrectionReason.Corrected)]
    [InlineData("hello", AutoCorrectionReason.AlreadyCorrect)]
    [InlineData("xqzxqzxqz", AutoCorrectionReason.UnknownWord)]
    [InlineData("abc_123", AutoCorrectionReason.ProtectedToken)]
    public async Task DiagnosticsExplainTheDecisionWithoutRetainingTypedWords(string keys, AutoCorrectionReason expected)
    {
        EnableAutomaticCorrection(native: true);
        _platform.Type(keys + " "); await _controller.AutoCorrectionIdleAsync();
        var status = await _controller.GetAutomaticStatusAsync();
        Assert.Equal(expected, Assert.Single(status.Recent).Reason);
        Assert.Equal(expected == AutoCorrectionReason.ProtectedToken ? DictionaryHealth.NotChecked : DictionaryHealth.Ready, status.Health);
        if (expected == AutoCorrectionReason.ProtectedToken) Assert.False(_controller.DictionaryIsOpen);
        var report = AutoCorrectionReport.Format(status);
        Assert.DoesNotContain(keys, report); Assert.DoesNotContain("original clipboard", report); Assert.DoesNotContain("editor", report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputOrManualCancellationIsReportedInsteadOfSuccessfulCorrection(bool manual)
    {
        EnableAutomaticCorrection(); _platform.Type("ghb ");
        if (manual) await _controller.CapturePersonalWordAsync(); else _platform.Type("x");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.Canceled, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task ExclusionsDisabledCorrectionAndLanguageRulesHaveDistinctReasons()
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false; _controller.ApplySettings(settings);
        _platform.Type("ghb ");
        Assert.Equal(AutoCorrectionReason.Disabled, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        settings.AutoCorrectEnabled = true; settings.ExcludedApps = ["editor"]; _controller.ApplySettings(settings);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.ExcludedApplication, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        settings.ExcludedApps = []; settings.Layouts = ["en"]; _controller.ApplySettings(settings);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.LanguageRestricted, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task DiagnosticHistoryIsBoundedAndCheckingSearchDoesNotCreatePersonalData()
    {
        var status = await _controller.GetAutomaticStatusAsync(); Assert.Equal(DictionaryHealth.NotChecked, status.Health);
        status = await _controller.CheckDictionaryAsync(); Assert.Equal(DictionaryHealth.Ready, status.Health);
        Assert.False(File.Exists(Path.Combine(_dir, "personal-words.sqlite3")));
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false; _controller.ApplySettings(settings);
        for (var i = 0; i < 50; i++) _platform.Type("privateword ");
        status = await _controller.GetAutomaticStatusAsync(); Assert.Equal(20, status.Recent.Length);
        Assert.DoesNotContain("privateword", AutoCorrectionReport.Format(status));
    }

    [Fact]
    public async Task CheckRestoresDictionarySearchAfterAReaderFailureWithoutRestartingTheController()
    {
        EnableAutomaticCorrection(native: true);
        var file = Path.Combine(_dir, "personal-words.sqlite3"); File.WriteAllText(file, "broken database fixture");
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        var status = await _controller.GetAutomaticStatusAsync(); Assert.Equal(DictionaryHealth.Failed, status.Health);
        Assert.Equal(AutoCorrectionReason.DictionaryUnavailable, status.Recent[0].Reason);
        File.Delete(file); // Model restoring the user's dictionary externally, never delete user data in production.
        status = await _controller.CheckDictionaryAsync(); Assert.Equal(DictionaryHealth.Ready, status.Health);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.Corrected, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        Assert.EndsWith("при ", _platform.Document.Text);
    }

    [Fact]
    public async Task CompetingPersonalSpellingsAreReportedAsAmbiguous()
    {
        EnableAutomaticCorrection();
        await _controller.SetPersonalWordAsync("hello", "en"); await _controller.SetPersonalWordAsync("руддщ", "ru");
        var settings = _controller.Settings.Clone(); settings.Layouts = ["en", "ru"]; _controller.ApplySettings(settings);
        _platform.ActiveLayoutId = "uk"; _platform.TypeAs("hello ", "руддщ "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.Ambiguous, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        Assert.Equal("руддщ ", _platform.Document.Text);
    }

    [Fact]
    public async Task IgnoredPasteIsNeverReportedAsAConfirmedReplacement()
    {
        EnableAutomaticCorrection(); _platform.DropNextPaste = true;
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb ", _platform.Document.Text); Assert.Equal("en", _platform.ActiveLayoutId);
        Assert.Equal(AutoCorrectionReason.Unconfirmed, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task ExternalClipboardChangeHasItsOwnReasonWithoutDisclosingTheCopy()
    {
        EnableAutomaticCorrection();
        _platform.AfterCopy = () => { _platform.AfterCopy = null; _platform.ExternalCopy("private external copy", owner: 99); };
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        var status = await _controller.GetAutomaticStatusAsync();
        Assert.Equal(AutoCorrectionReason.ClipboardChanged, status.Recent[0].Reason);
        Assert.Equal("private external copy", _platform.ClipboardText);
        Assert.DoesNotContain("private external copy", AutoCorrectionReport.Format(status));
    }

    [Fact]
    public async Task SettingsPauseIsVisibleBeforeAnyAttemptAndResumesAfterClosingSettings()
    {
        EnableAutomaticCorrection(); _controller.SuspendHotkeys();
        var status = await _controller.GetAutomaticStatusAsync();
        Assert.True(status.Enabled); Assert.Equal(AutoCorrectionReason.SettingsOpen, status.PauseReason);
        _platform.Type("ghb ");
        Assert.Equal(AutoCorrectionReason.SettingsOpen, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        _controller.RegisterHotkeys();
        Assert.Null((await _controller.GetAutomaticStatusAsync()).PauseReason);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(AutoCorrectionReason.Corrected, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task DictionaryExportNeverOverwritesTheApplicationsOwnSettings()
    {
        var settings = Path.Combine(_dir, "settings.json"); var before = File.ReadAllBytes(settings);
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.ExportPersonalWordsAsync(settings));
        Assert.Equal(before, File.ReadAllBytes(settings));
    }
}

public sealed class DictionaryProtocolTests
{
    [Fact]
    public async Task DictionaryPagesAndPrivacySafeStatusRoundTripThroughTheActualRuntimeChannel()
    {
        using var channel = new RuntimeChannel("TD." + Guid.NewGuid().ToString("N"), request =>
        {
            Assert.Equal("personal-query", request.Command); Assert.Equal("на", request.WordQuery?.Prefix);
            return Task.FromResult(new RuntimeReply(true, WordPage: new([new("напиши", "ru")], 1, 0, 50),
                AutoCorrection: new(true, true, DictionaryHealth.Ready, [new(DateTimeOffset.UtcNow, AutoCorrectionReason.Canceled, 155, "en", "ru")], AutoCorrectionReason.SettingsOpen)));
        });
        var reply = await new RuntimeClient(channel.Name).CallAsync(new("personal-query", WordQuery: new("на", "ru")));
        Assert.Equal("напиши", reply.WordPage?.Items[0].Word);
        Assert.Equal(AutoCorrectionReason.SettingsOpen, reply.AutoCorrection?.PauseReason);
        Assert.Equal(AutoCorrectionReason.Canceled, reply.AutoCorrection?.Recent[0].Reason);
        Assert.DoesNotContain("напиши", AutoCorrectionReport.Format(reply.AutoCorrection!));
    }
}
