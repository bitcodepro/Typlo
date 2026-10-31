using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Platform.Shared;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Storage;
using Typlo.Core.Text;

namespace Typlo.App.Services;

/// <summary>
/// End-to-end checks against the real clipboard, hotkeys and keyboard input of the current OS:
/// <c>Typlo --selftest report.txt</c>. Uses a temporary data folder and restores the clipboard afterwards.
/// </summary>
internal static partial class SelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var lines = new List<string>();
        var failures = 0;
        var dir = Path.Combine(Path.GetTempPath(), "typlo-selftest-" + Guid.NewGuid().ToString("N"));
        using var platform = PlatformFactory.Create();
        if (platform is HookPlatformBase hook) hook.AcceptSimulatedHotkeys = true;
        TypingEvents.AcceptSimulated = true;
        lines.Add($"Platform: {platform.Name}");
        var caps = platform.Capabilities;
        var userClipboard = platform.Snapshot();
        AppController? controller = null;

        async Task Check(string name, Func<Task<string?>> test, bool when = true)
        {
            var filter = Environment.GetEnvironmentVariable("TYPLO_SELFTEST_FILTER");
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            if (!when) { lines.Add($"SKIP {name}"); return; }
            try
            {
                var error = await test();
                lines.Add(error is null ? $"PASS {name}" : $"FAIL {name}: {error}");
                if (error is not null) failures++;
            }
            catch (Exception e)
            {
                lines.Add($"FAIL {name}: {e.GetType().Name}: {e.Message}");
                failures++;
            }
        }

        try
        {
            controller = new AppController(platform, new AppPaths(dir), PlatformFactory.CreateKeyProtector(diagnostic: true), diagnostic: true);
            // CI commonly has only English installed. The RU/EN text fixtures need both maps,
            // but must not change the user's configured/system-active input sources.
            var nativeLayouts = controller.AvailableLayouts;
            controller.ConfigureDiagnosticLayouts(nativeLayouts.Concat(new[] { KeyboardLayouts.English, KeyboardLayouts.Russian }
                .Where(fixture => !nativeLayouts.Any(l => l.SupportsConversion && l.LanguageCode == fixture.LanguageCode))));
            controller.ApplySettings(Tweak(controller.Settings));

            await Check("every interface language has every string with the same placeholders", () =>
            {
                var problems = new List<string>();
                var saved = Loc.Current;
                foreach (var (code, _) in Loc.Languages)
                {
                    foreach (var key in Loc.MissingKeys(code)) problems.Add($"{code}: missing {key}");
                    Loc.SetLanguage(code);
                    foreach (var key in Loc.AllKeys)
                    {
                        Loc.SetLanguage("en");
                        var english = Placeholders(Loc.T(key));
                        Loc.SetLanguage(code);
                        var translated = Loc.T(key);
                        if (!english.SetEquals(Placeholders(translated))) problems.Add($"{code}: placeholders differ in {key}");
                        if (string.IsNullOrWhiteSpace(translated)) problems.Add($"{code}: empty {key}");
                    }
                }
                Loc.SetLanguage(saved);
                return Task.FromResult(problems.Count == 0 ? null : string.Join("; ", problems.Take(10)));
            });

            await Check("clipboard text round-trip", () =>
            {
                platform.SetText("Привет, clipboard ✓");
                var back = platform.ReadText();
                return Task.FromResult(back == "Привет, clipboard ✓" ? null : $"got '{back}'");
            });

            await Check("dictionary collection switches reject stale selection during delayed and failed queries",
                () => CheckDictionaryCollectionSwitchesAsync(controller));

            await Check("manual spelling popup preserves editor focus and supports apply and Undo", async () =>
            {
                Views.ToolsWindow? popup = null;
                try
                {
                    return await TransformSequenceInTextBox(platform, "", [
                        (async () =>
                        {
                            var target = platform.ForegroundWindow;
                            var english = controller.AvailableLayouts.First(l => l.LanguageCode == "en" && l.SupportsConversion);
                            platform.SwitchKeyboardLayout(target, english);
                            foreach (var c in "ghb") UserChar(platform, c);
                            await Task.Delay(100);
                            await controller.CycleChoicesAsync();
                            if ((await controller.GetToolsStatusAsync()).Choices.Length == 0) throw new InvalidOperationException("No spelling candidates");
                            await Dispatcher.UIThread.InvokeAsync(() => { popup = new(controller, true); popup.Show(); });
                            await Task.Delay(100);
                            if (platform.ForegroundWindow != target) throw new InvalidOperationException("Candidate popup activated instead of the editor");
                            await controller.ApplyChoiceAsync();
                        }, "при"),
                        (() => controller.UndoLastTransformAsync(), "ghb")], select: false);
                }
                finally { await Dispatcher.UIThread.InvokeAsync(() => popup?.Close()); }
            }, when: caps.SimulateInput && platform.Capabilities.SwitchLayout);

            await Check("own writes are not captured", async () =>
            {
                platform.SetText("own-write-should-not-be-stored");
                await Task.Delay(1500);
                return controller.Store.List().Any(e => e.PreviewText.Contains("own-write")) ? "was stored" : null;
            });

            await Check("system layouts provide native identities and physical-key maps", () =>
            {
                var installed = platform.GetKeyboardLayouts();
                return Task.FromResult<string?>(installed.Count == 0 ? "no installed layouts discovered" :
                    installed.Any(l => l.Normal.Length != KeyboardLayouts.English.Normal.Length) ? "physical key counts differ" : null);
            });

            await Check("snapshot/restore", () =>
            {
                platform.SetText("original");
                var snap = platform.Snapshot();
                platform.SetText("temporary");
                platform.Restore(snap);
                var back = platform.ReadText();
                return Task.FromResult(back == "original" ? null : $"got '{back}'");
            });

            await Check("external text copy is captured (encrypted store)", async () =>
            {
                var marker = "selftest-" + Guid.NewGuid().ToString("N")[..8];
                await using var copy = await External.CopyTextAsync(marker);
                return await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == marker), 5000)
                    ? null : "acknowledged external text was not captured within 5 s";
            });

            await Check("external image copy is captured with thumbnail", async () =>
            {
                await using var copy = await External.CopyImageAsync(await MakePng(200, 120));
                var ok = await WaitFor(() => controller.Store.List().Any(e => e is { Kind: ClipKind.Image, Width: 200, Height: 120, Thumbnail.Length: > 0 }), 5000);
                return ok ? null : "image not captured";
            });

            await Check("image from history goes back to clipboard", async () =>
            {
                var img = controller.Store.List().FirstOrDefault(e => e.Kind == ClipKind.Image);
                if (img is null) return "no image in history";
                platform.SetText("placeholder");
                await controller.PasteAsync(img.Id, 0, paste: false);
                await Task.Delay(300);
                var size = External.ImageSize();
                return size == "200x120" ? null : $"clipboard image is '{size}'";
            });

            await Check("excluded app is not stored", async () =>
            {
                const string marker = "excluded-secret-123";
                var payload = Path.Combine(dir, "excluded-copy.txt");
                await File.WriteAllTextAsync(payload, marker);
                using var producer = Process.Start(External.SelfStart("--selftest-text-copy", payload))!;
                var error = producer.StandardError.ReadToEndAsync();
                var observed = new TaskCompletionSource<RawClip>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Captured(RawClip clip) { if (clip.Text == marker) observed.TrySetResult(clip); }
                var saved = controller.Settings.Clone();
                platform.ClipboardCaptured += Captured;
                try
                {
                    var excluded = saved.Clone(); excluded.ExcludedApps = [producer.ProcessName];
                    controller.ApplySettings(excluded);
                    await producer.StandardInput.WriteLineAsync("copy"); await producer.StandardInput.FlushAsync();
                    if (await producer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "COPIED")
                        throw new IOException("Native text producer did not acknowledge Copy: " + await error);
                    var clip = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Platform.Windows.Native.GetWindowThreadProcessId(Platform.Windows.Native.GetClipboardOwner(), out var owner);
                    if (owner != producer.Id || !string.Equals(clip.SourceApp, producer.ProcessName, StringComparison.OrdinalIgnoreCase))
                        return "copy did not retain the excluded producer as its owner";
                    // Our observer runs after the controller's synchronous recording handler.
                    return controller.Store.List().Any(e => e.PreviewText == marker) ? "excluded copy was stored" : null;
                }
                finally
                {
                    platform.ClipboardCaptured -= Captured; controller.ApplySettings(saved);
                    try
                    {
                        if (!producer.HasExited)
                        {
                            await producer.StandardInput.WriteLineAsync("exit"); await producer.StandardInput.FlushAsync();
                            await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                    finally { if (!producer.HasExited) producer.Kill(entireProcessTree: true); File.Delete(payload); }
                }
            }, when: OperatingSystem.IsWindows());

            await Check("global hotkey fires on key press", async () =>
            {
                var fired = new TaskCompletionSource();
                void OnPressed(int id) { if (id == 4242) fired.TrySetResult(); }
                platform.HotkeyPressed += OnPressed;
                try
                {
                    if (!platform.RegisterHotkey(4242, new Hotkey(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "F11")))
                        return "could not register Ctrl+Alt+Shift+F11";
                    await Task.Delay(300);
                    platform.SendChord(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "F11");
                    var done = await Task.WhenAny(fired.Task, Task.Delay(3000));
                    return done == fired.Task ? null : "hotkey did not fire";
                }
                finally
                {
                    platform.HotkeyPressed -= OnPressed;
                    controller.RegisterHotkeys();
                }
            }, when: caps.GlobalHotkeys);

            await Check("convert selected text in a real text box (ghbdtn → привет)", () => ConvertInTextBox(controller, platform,
                "ghbdtn vbh", KeyboardLayouts.Russian, "привет мир"), when: caps.SimulateInput);
            await Check("convert back (руддщ → hello)", () => ConvertInTextBox(controller, platform,
                "руддщ", KeyboardLayouts.English, "hello"), when: caps.SimulateInput);
            await Check("conversion restores the user's clipboard", async () =>
            {
                platform.SetText("keep-me");
                var err = await ConvertInTextBox(controller, platform, "ntcn", KeyboardLayouts.Russian, "тест");
                var back = platform.ReadText();
                return err ?? (back == "keep-me" ? null : $"clipboard is '{back}'");
            }, when: caps.SimulateInput);

            await Check("last word is converted when nothing is selected", () => ConvertInTextBox(controller, platform,
                "Привет ghbdtn", KeyboardLayouts.Russian, "Привет привет", selectAll: false), when: caps.SimulateInput);
            await Check("last word includes punctuation that is a letter in the target layout", () => ConvertInTextBox(controller, platform,
                "Привет htdm.dftim", KeyboardLayouts.Russian, "Привет ревьюваешь", selectAll: false), when: caps.SimulateInput);
            await Check("only newly typed punctuation is converted next to an existing word", () => ConvertInTextBox(controller, platform,
                "готовое?", KeyboardLayouts.Russian, "готовое,", selectAll: false, typedSuffix: "?"), when: caps.SimulateInput);

            await Check("consecutive case commands cycle in the same text box without reselection", () =>
                TransformSequenceInTextBox(platform, "hello world",
                    [(controller.ChangeCaseAsync, "Hello World"), (controller.ChangeCaseAsync, "HELLO WORLD"),
                     (controller.ChangeCaseAsync, "hello world")]), when: caps.SimulateInput);

            await Check("consecutive transliteration commands reselect results with different lengths", () =>
                TransformSequenceInTextBox(platform, "щука",
                    [(controller.TransliterateAsync, "shchuka"), (controller.TransliterateAsync, "щука")]), when: caps.SimulateInput);

            await Check("consecutive layout commands toggle in the same text box without reselection", () =>
                TransformSequenceInTextBox(platform, "ghbdtn",
                    [(() => controller.ConvertSelectionAsync(null), "привет"),
                     (() => controller.ConvertSelectionAsync(null), "ghbdtn")]), when: caps.SimulateInput);

            await Check("snippet trigger typed in a text box is replaced", async () =>
            {
                var s = controller.Settings.Clone();
                s.Snippets = [new Snippet { Trigger = ";hi", Text = "Hello there" }];
                controller.ApplySettings(s);
                await Task.Delay(500); // the typing hook starts asynchronously
                try
                {
                    return await TransformInTextBox(platform, "", async () =>
                    {
                        foreach (var key in new[] { "Semicolon", "H", "I" })
                        {
                            UserKey(platform, key);
                            await Task.Delay(60);
                        }
                        await Task.Delay(1500);
                    }, "Hello there", select: false);
                }
                finally
                {
                    s.Snippets = [];
                    controller.ApplySettings(s);
                }
            }, when: caps.SimulateInput);

            await Check("multiline snippet preserves indentation and places the cursor marker", () =>
                TransformInTextBox(platform, ";cur", () => controller.ExpandSnippetAsync(
                    new Snippet { Trigger = ";cur", Text = "first\n    {cursor}\nlast" }, platform.ForegroundWindow),
                    "first\n    \nlast", select: false, multiline: true, expectedBeforeCaret: "first\n    "), when: caps.SimulateInput);

            await Check("undo puts back the text replaced by the last conversion", () =>
                TransformInTextBox(platform, "ghbdtn", async () =>
                {
                    await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                    await Task.Delay(300);
                    await controller.UndoLastTransformAsync();
                }, "ghbdtn"), when: caps.SimulateInput);

            await Check("a copy made during a conversion is kept (clipboard not restored over it)", () =>
                TransformInTextBox(platform, "keep", async () =>
                {
                    platform.SetText("before-conversion");
                    await controller.TransformSelectionAsync(text =>
                    {
                        platform.SetText("copied-meanwhile"); // stands for the user copying something during the operation
                        return (text, null);
                    });
                    var now = platform.ReadText();
                    if (now != "copied-meanwhile") throw new InvalidOperationException($"clipboard is '{now}'");
                }, "keep"), when: caps.SimulateInput);

            await Check("undo does nothing after the user typed more text", () =>
                TransformInTextBox(platform, "abc", async () =>
                {
                    await controller.ChangeCaseAsync(); // abc -> Abc
                    await Task.Delay(200);
                    foreach (var key in new[] { "1", "2", "3" }) // digits: the same in every keyboard layout
                    {
                        UserKey(platform, key);
                        await Task.Delay(40);
                    }
                    await Task.Delay(200);
                    await controller.UndoLastTransformAsync();
                }, "Abc123"), when: caps.SimulateInput);

            await Check("nothing is pasted if another app copies right before the paste", () =>
                TransformInTextBox(platform, "keep2", async () =>
                {
                    await controller.TransformSelectionAsync(text =>
                    {
                        platform.SetText("copied-before-paste"); // another app copies after Typlo read the selection
                        return ("CHANGED", null);
                    });
                    var now = platform.ReadText();
                    if (now != "copied-before-paste") throw new InvalidOperationException($"clipboard is '{now}'");
                }, "keep2"), when: caps.SimulateInput);

            // X11 names the owner through X-Resource: copies are then checked like on Windows. Typlo's own writes go
            // through xclip, a separate process, so they never count as the text box's copy.
            await Check("the clipboard owner is known (X11)", () =>
                TransformInTextBox(platform, "owner", async () =>
                {
                    var window = platform.ForegroundWindow;
                    platform.SetText("written-by-xclip");
                    if (platform.IsClipboardOwnedBy(window) is var before && before != false)
                        throw new InvalidOperationException($"owned by the text box after an xclip write: {before?.ToString() ?? "unknown"}");
                    platform.SendCopy();
                    if (!await WaitFor(() => platform.ReadText() == "owner")) throw new InvalidOperationException("the copy did not arrive");
                    if (platform.IsClipboardOwnedBy(window) is var after && after != true)
                        throw new InvalidOperationException($"not owned by the text box after its copy: {after?.ToString() ?? "unknown"}");
                }, "owner"), when: caps.SimulateInput && platform.Name == "Linux (X11)");

            await Check("a selected line with its line break is converted", () =>
                TransformInTextBox(platform, "ghbdtn\n", () => controller.ConvertSelectionAsync(KeyboardLayouts.Russian),
                    "привет\n", multiline: true), when: caps.SimulateInput);

            await Check("a snippet does nothing if the window changed before it ran", () =>
                TransformInTextBox(platform, "OTHER", () => controller.ExpandSnippetAsync(new Snippet { Trigger = ";zz", Text = "EXPANDED" }, 12345),
                    "OTHER", select: false), when: caps.SimulateInput);

            await Check("diagnostic runs never change autostart", () =>
            {
                var before = platform.AutostartEnabled;
                var s = controller.Settings.Clone();
                s.StartWithSystem = !before;
                controller.ApplySettings(s);
                var after = platform.AutostartEnabled;
                s.StartWithSystem = false;
                controller.ApplySettings(s);
                return Task.FromResult(after == before ? null : $"autostart changed from {before} to {after}");
            });

            await Check("second press pastes the previous item", async () =>
            {
                await using var olderCopy = await External.CopyTextAsync("older-item-55");
                if (!await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == "older-item-55"), 5000)) return "older not captured";
                await using var newerCopy = await External.CopyTextAsync("newer-item-55");
                if (!await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == "newer-item-55"), 5000)) return "newer not captured";
                await controller.PastePreviousAsync(0, paste: false);
                var text = platform.ReadText();
                var order = string.Join(" | ", controller.Store.List().OrderByDescending(e => e.LastUsed).Take(3).Select(e => $"{e.Kind}:{e.PreviewText}"));
                return text == "older-item-55" ? null : $"clipboard is '{text}'; newest: {order}";
            });

            await Check("text is read from an image (OCR)", async () =>
            {
                var png = await MakeTextPng("CLIPBOARD 2026");
                var text = await OcrService.RecognizeAsync(png);
                return text?.Contains("CLIPBOARD", StringComparison.OrdinalIgnoreCase) == true && text.Contains("2026") ? null : $"OCR returned '{text}'";
            }, when: OcrService.Available);

            await Check("keyboard layout group switches (Linux X11)", () =>
            {
                if (!OperatingSystem.IsLinux()) return Task.FromResult<string?>(null);
                var layouts = Platform.Linux.XkbLayouts.ConfiguredLayouts().ToList();
                var ru = layouts.IndexOf("ru");
                var en = layouts.FindIndex(l => l is "us" or "gb");
                if (ru < 0 || en < 0) return Task.FromResult<string?>($"layouts are '{string.Join(",", layouts)}'");
                if (!platform.SwitchKeyboardLayout(0, 0x0419)) return Task.FromResult<string?>("switch to ru failed");
                var afterRu = Platform.Linux.XkbLayouts.CurrentGroup();
                platform.SwitchKeyboardLayout(0, 0x0409);
                var afterEn = Platform.Linux.XkbLayouts.CurrentGroup();
                return Task.FromResult(afterRu == ru && afterEn == en ? null : $"groups: ru {afterRu}/{ru}, en {afterEn}/{en}");
            }, when: OperatingSystem.IsLinux() && caps.SwitchLayout);

            await Check("formatted text keeps HTML; Shift+Enter pastes plain text", async () =>
            {
                if (!OperatingSystem.IsWindows()) return null;
                var marker = "fmt-" + Guid.NewGuid().ToString("N")[..6];
                await External.SetHtmlWindowsAsync($"<b>{marker}</b>", marker);
                if (!await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == marker && e.HasFormatting), 5000))
                    return "formatted text not captured";
                var id = controller.Store.List().First(e => e.PreviewText == marker).Id;
                await controller.PasteAsync(id, 0, paste: false);
                var html = await External.ReadHtmlWindowsAsync();
                if (!html.Contains($"<b>{marker}</b>")) return $"HTML not restored: '{html}'";
                await controller.PasteAsync(id, 0, paste: false, plainText: true);
                var plain = await External.ReadHtmlWindowsAsync();
                return plain.Length == 0 && platform.ReadText() == marker ? null : $"plain paste still has HTML: '{plain}'";
            }, when: OperatingSystem.IsWindows());

            await Check("copied files are stored and pasted back as files", async () =>
            {
                var dirForFiles = Path.Combine(dir, "files");
                Directory.CreateDirectory(dirForFiles);
                var a = Path.Combine(dirForFiles, "report one.txt");
                var b = Path.Combine(dirForFiles, "photo.txt");
                File.WriteAllText(a, "a");
                File.WriteAllText(b, "b");
                External.SetFiles([a, b]);
                if (!await WaitFor(() => controller.Store.List().Any(e => e.Kind == ClipKind.Files && e.Files.Count == 2), 5000))
                    return "files not captured";
                var id = controller.Store.List().First(e => e.Kind == ClipKind.Files).Id;
                platform.SetText("placeholder");
                await controller.PasteAsync(id, 0, paste: false);
                var back = await External.ReadFilesAsync();
                return back.Order().SequenceEqual(new[] { a, b }.Order()) ? null : $"clipboard files: [{string.Join(", ", back)}]";
            });

            await Check("paused history stores nothing, resume stores again", async () =>
            {
                controller.PauseHistory(TimeSpan.FromMinutes(1));
                await using var pausedCopy = await External.CopyTextAsync("paused-secret-777");
                await Task.Delay(2000);
                var stored = controller.Store.List().Any(e => e.PreviewText == "paused-secret-777");
                controller.ResumeHistory();
                if (stored) return "stored while paused";
                await using var resumedCopy = await External.CopyTextAsync("after-resume-777");
                return await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == "after-resume-777"), 5000)
                    ? null : "not stored after resume";
            });

            await Check("keep-awake request is accepted by the OS", () =>
                Task.FromResult(platform.SetKeepAwake(true) && platform.SetKeepAwake(false) ? null : "keep-awake failed"));

            await Check("activity signal resets the system idle timer (Teams presence)", async () =>
            {
                if (!OperatingSystem.IsWindows()) { platform.SignalUserActivity(); return null; }
                await Task.Delay(1500);
                var before = IdleMs();
                if (before < 1000) return null; // the user is typing; nothing to measure
                Platform.Windows.WinPlatform.SendActivityKey();
                await Task.Delay(100);
                var after = IdleMs();
                return after < before ? null : $"idle {before} ms -> {after} ms";
            });

            await Check("history file does not contain plaintext", () =>
            {
                var all = string.Concat(Directory.GetFiles(dir).Where(f => f.Contains("history.db")).Select(f =>
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return new StreamReader(fs).ReadToEnd();
                }));
                return Task.FromResult(all.Contains("selftest-") ? "plaintext marker found in db" : null);
            });
        }
        catch (Exception e)
        {
            lines.Add($"FAIL setup: {e}");
            failures++;
        }
        finally
        {
            controller?.Dispose();
            try { platform.Restore(userClipboard); } catch (Exception) { }
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        lines.Add(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        await File.WriteAllLinesAsync(reportPath, lines);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A key press the typing hook must see as the user's (Typlo filters its own injected keys).</summary>
    private static void UserKey(IPlatform platform, string key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (platform is HookPlatformBase hook) hook.SendChordAsUser(modifiers, key);
        else platform.SendChord(modifiers, key); // Windows: accepted via TypingEvents.AcceptSimulated
    }

    private static void UserChar(IPlatform platform, char c)
    {
        if (c == ' ') { UserKey(platform, "Space"); return; }
        var layout = KeyboardLayouts.English;
        var code = layout.CodeOf(c);
        if (code < 0) throw new ArgumentException("The self-test types physical US keys only.");
        var normal = layout.Normal[code % layout.Normal.Length];
        var name = char.IsAsciiLetterOrDigit(normal) ? char.ToUpperInvariant(normal).ToString() : normal switch
        {
            '.' => "Period", ',' => "Comma", '/' => "Slash", ';' => "Semicolon", '\'' => "Quote",
            '[' => "OpenBracket", ']' => "CloseBracket", '\\' => "Backslash", '`' => "Tilde",
            '-' => "Minus", '=' => "Plus", _ => throw new ArgumentException("Unknown physical key."),
        };
        UserKey(platform, name, code >= layout.Normal.Length ? KeyModifiers.Shift : KeyModifiers.None);
    }

    private static HashSet<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).ToHashSet();

    private static uint IdleMs()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var info = new Platform.Windows.Native.LASTINPUTINFO { cbSize = 8 };
        Platform.Windows.Native.GetLastInputInfo(ref info);
        return (uint)Platform.Windows.Native.GetTickCount64() - info.dwTime;
    }

    private static Core.Settings.AppSettings Tweak(Core.Settings.AppSettings s)
    {
        // Self-test must not grab the user's real shortcuts or touch autostart.
        s.ToolsHotkey = s.ChoicesHotkey = s.AcceptChoiceHotkey = s.CancelChoiceHotkey = "";
        s.ToggleHotkey = "";
        s.CaseHotkey = ""; s.TranslitHotkey = ""; s.UndoHotkey = ""; s.AddWordHotkey = ""; s.LayoutSwitchHotkey = "";
        s.AutoCorrectEnabled = false;
        s.ClipboardHotkey = "";
        s.ScreenshotCopyHotkey = "";
        s.ScreenshotSaveHotkey = "";
        s.LayoutHotkeys = [];
        s.SwitchSystemLayout = false;
        s.Layouts = ["en", "ru"]; // fixture cycle for the RU/EN text assertions, resolved to native IDs
        s.StartWithSystem = false;
        s.ExcludedApps = [];
        return s;
    }

    private static async Task<byte[]> MakePng(int w, int h) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(w, h));
        using (var ctx = bmp.CreateDrawingContext())
            ctx.FillRectangle(Avalonia.Media.Brushes.OrangeRed, new Rect(0, 0, w, h));
        using var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    });

    private static Task<byte[]> MakeTextPng(string text) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(900, 200));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(Avalonia.Media.Brushes.White, new Rect(0, 0, 900, 200));
            var ft = new Avalonia.Media.FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                Avalonia.Media.FlowDirection.LeftToRight, new Avalonia.Media.Typeface("Inter"), 72, Avalonia.Media.Brushes.Black);
            ctx.DrawText(ft, new Point(40, 50));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }).GetTask();

    /// <summary>Puts <paramref name="input"/> in a focused text box, runs <paramref name="action"/> and compares the result.</summary>
    private static Task<string?> TransformInTextBox(IPlatform platform, string input, Func<Task> action, string expected,
        bool select = true, bool multiline = false, string? expectedBeforeCaret = null) =>
        TransformSequenceInTextBox(platform, input, [(action, expected)], select, multiline, expectedBeforeCaret);

    private static async Task<string?> TransformSequenceInTextBox(IPlatform platform, string input,
        (Func<Task> Action, string Expected)[] steps, bool select = true, bool multiline = false, string? expectedBeforeCaret = null)
    {
        Window? window = null;
        TextBox? box = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            box = new TextBox { Text = input, AcceptsReturn = multiline };
            window = new Window { Width = 400, Height = 120, Content = box, Title = "Typlo self-test", Topmost = true };
            window.Show();
            window.Activate();
        });
        try
        {
            await Task.Delay(500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (OperatingSystem.IsWindows() && window!.TryGetPlatformHandle()?.Handle is { } h) platform.ActivateWindow(h);
                window!.Activate();
                box!.Focus();
                if (select) box.SelectAll();
                else box.CaretIndex = input.Length;
            });
            await Task.Delay(300);
            for (var i = 0; i < steps.Length; i++)
            {
                await steps[i].Action();
                await Task.Delay(300);
                var text = (await Dispatcher.UIThread.InvokeAsync(() => box!.Text))?.Replace("\r\n", "\n");
                if (text != steps[i].Expected) return $"step {i + 1}: text box has '{text}', expected '{steps[i].Expected}'";
            }
            if (expectedBeforeCaret is not null)
            {
                var before = await Dispatcher.UIThread.InvokeAsync(() => (box!.Text ?? "")[..box.CaretIndex].Replace("\r\n", "\n"));
                if (before != expectedBeforeCaret) return "caret is not at the cursor marker";
            }
            return null;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close());
        }
    }

    private static async Task<string?> ConvertInTextBox(AppController controller, IPlatform platform,
        string input, KeyboardLayout target, string expected, bool selectAll = true, string? typedSuffix = null)
    {
        if (!selectAll && typedSuffix is null)
        {
            var start = input.Length;
            while (start > 0 && !char.IsWhiteSpace(input[start - 1])) start--;
            typedSuffix = input[start..];
        }
        var initial = selectAll ? input : input[..^typedSuffix!.Length];
        Window? window = null;
        TextBox? box = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            box = new TextBox { Text = initial, AcceptsReturn = false };
            window = new Window { Width = 400, Height = 120, Content = box, Title = "Typlo self-test", Topmost = true };
            window.Show();
            window.Activate();
        });
        try
        {
            await Task.Delay(500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (OperatingSystem.IsWindows() && window!.TryGetPlatformHandle()?.Handle is { } h) platform.ActivateWindow(h);
                window!.Activate();
                box!.Focus();
                if (selectAll) box.SelectAll();
                else
                {
                    box.ClearSelection();
                    box.CaretIndex = initial.Length;
                }
            });
            await Task.Delay(300);
            if (!selectAll)
            {
                foreach (var c in typedSuffix!) { UserChar(platform, c); await Task.Delay(20); }
                await Task.Delay(100); // let the hook report the user's input before issuing the command
            }
            await controller.ConvertSelectionAsync(target);
            await Task.Delay(300);
            var text = await Dispatcher.UIThread.InvokeAsync(() => box!.Text);
            return text == expected ? null : $"text box has '{text}'";
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close());
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Puts things on the clipboard from another process, the way a real app would.</summary>
    private static class External
    {
        public static ProcessStartInfo SelfStart(params string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Typlo.dll"));
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }
        public static async Task<ClipboardProducer> CopyTextAsync(string text)
        {
            if (OperatingSystem.IsWindows()) return await ClipboardProducer.StartAsync("--selftest-text-copy", System.Text.Encoding.UTF8.GetBytes(text));
            else if (OperatingSystem.IsMacOS()) Proc.Run("/usr/bin/pbcopy", [], System.Text.Encoding.UTF8.GetBytes(text));
            else Proc.Run("xclip", ["-selection", "clipboard", "-t", "UTF8_STRING", "-i"], System.Text.Encoding.UTF8.GetBytes(text), captureOutput: false);
            return new(null, null);
        }

        public static async Task<ClipboardProducer> CopyImageAsync(byte[] png)
        {
            if (OperatingSystem.IsWindows()) return await ClipboardProducer.StartAsync("--selftest-image-copy", png);
            var file = Path.Combine(Path.GetTempPath(), "typlo-selftest.png");
            File.WriteAllBytes(file, png);
            if (OperatingSystem.IsMacOS())
                Proc.Run("/usr/bin/osascript", ["-e", $"set the clipboard to (read (POSIX file \"{file}\") as «class PNGf»)"]);
            else Proc.Run("xclip", ["-selection", "clipboard", "-t", "image/png", "-i"], png, captureOutput: false);
            return new(null, null);
        }

        internal sealed class ClipboardProducer(Process? process, string? payload) : IAsyncDisposable
        {
            internal static async Task<ClipboardProducer> StartAsync(string mode, byte[] data)
            {
                var file = Path.Combine(Path.GetTempPath(), "typlo-copy-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(file, data);
                Process? child;
                try { child = Process.Start(SelfStart(mode, file)) ?? throw new IOException("Cannot start clipboard producer"); }
                catch { File.Delete(file); throw; }
                var lease = new ClipboardProducer(child, file);
                var errors = child.StandardError.ReadToEndAsync();
                try
                {
                    await child.StandardInput.WriteLineAsync("copy"); await child.StandardInput.FlushAsync();
                    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "COPIED")
                        throw new IOException("Clipboard producer failed: " + await errors);
                    return lease;
                }
                catch { await lease.DisposeAsync(); throw; }
            }
            public async ValueTask DisposeAsync()
            {
                if (process is null) return;
                try
                {
                    if (!process.HasExited)
                    {
                        await process.StandardInput.WriteLineAsync("exit"); await process.StandardInput.FlushAsync();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                finally
                {
                    try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                    finally { process.Dispose(); if (payload is not null) File.Delete(payload); }
                }
            }
        }

        public static async Task SetHtmlWindowsAsync(string fragment, string plain)
        {
            // CF_HTML needs a header with byte offsets of the HTML and the fragment.
            const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            var pre = "<html><body><!--StartFragment-->";
            var post = "<!--EndFragment--></body></html>";
            var headerLength = string.Format(header, 0, 0, 0, 0).Length;
            var startHtml = headerLength;
            var startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(pre);
            var endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(fragment);
            var endHtml = endFragment + System.Text.Encoding.UTF8.GetByteCount(post);
            var cfHtml = string.Format(header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
            var file = Path.Combine(Path.GetTempPath(), "typlo-selftest-html-" + Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(file, cfHtml);
            try
            {
                var start = SelfStart("--selftest-html-copy", file, plain);
                using var producer = Process.Start(start)!;
                var error = producer.StandardError.ReadToEndAsync();
                var output = producer.StandardOutput.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await producer.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { producer.Kill(entireProcessTree: true); throw new TimeoutException("HTML clipboard producer timed out"); }
                await output;
                if (producer.ExitCode != 0) throw new IOException("HTML clipboard producer failed: " + await error);
                await error;
            }
            finally { File.Delete(file); }
        }

        public static async Task<string> ReadHtmlWindowsAsync() => (await RunPowerShellAsync("""
            Add-Type -AssemblyName System.Windows.Forms
            $h = [System.Windows.Forms.Clipboard]::GetData('HTML Format'); if ($h) { $h } else { '' }
            """)).Trim();

        public static void SetFiles(IReadOnlyList<string> paths)
        {
            if (OperatingSystem.IsWindows())
                _ = RunPowerShellAsync("Set-Clipboard -Path " + string.Join(',', paths.Select(p => $"'{p}'")) + "; Start-Sleep -Seconds 1");
            else if (OperatingSystem.IsMacOS())
                Proc.Run("/usr/bin/osascript", ["-e", "set the clipboard to {" + string.Join(", ", paths.Select(p => $"POSIX file \"{p}\"")) + "}"]);
            else
                Proc.Run("xclip", ["-selection", "clipboard", "-t", "text/uri-list", "-i"],
                    System.Text.Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => new Uri(p).AbsoluteUri + "\r\n"))), captureOutput: false);
        }

        public static async Task<List<string>> ReadFilesAsync()
        {
            string text;
            if (OperatingSystem.IsWindows())
                text = await RunPowerShellAsync("Get-Clipboard -Format FileDropList | ForEach-Object { $_.FullName }");
            else if (OperatingSystem.IsMacOS())
                text = Proc.Run("/usr/bin/osascript", ["-e", "set out to \"\"\nrepeat with f in (the clipboard as «class furl»)\nset out to out & POSIX path of f & linefeed\nend repeat\nout"]).Text;
            else
                text = string.Join('\n', Proc.Run("xclip", ["-selection", "clipboard", "-t", "text/uri-list", "-o"]).Text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(u => new Uri(u).LocalPath));
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        public static string ImageSize()
        {
            if (OperatingSystem.IsWindows())
                return RunPowerShellAsync("""
                    Add-Type -AssemblyName System.Windows.Forms
                    $i = [System.Windows.Forms.Clipboard]::GetImage(); if ($i) { "$($i.Width)x$($i.Height)" } else { "none" }
                    """).Result.Trim();
            var png = OperatingSystem.IsMacOS()
                ? PngFromMac()
                : Proc.Run("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]).Output;
            if (png is not { Length: > 0 }) return "none";
            using var bmp = new Bitmap(new MemoryStream(png));
            return $"{bmp.PixelSize.Width}x{bmp.PixelSize.Height}";
        }

        private static byte[]? PngFromMac()
        {
            var file = Path.Combine(Path.GetTempPath(), "typlo-selftest-out.png");
            var script = $"set f to open for access POSIX file \"{file}\" with write permission\nset eof f to 0\n" +
                         "write (the clipboard as «class PNGf») to f\nclose access f";
            return Proc.Run("/usr/bin/osascript", ["-e", script]).Ok ? File.ReadAllBytes(file) : null;
        }

        public static async Task<string> RunPowerShellAsync(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-STA", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; " + script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw new TimeoutException("Clipboard test producer timed out"); }
            if (p.ExitCode != 0) throw new IOException("Clipboard test producer failed: " + await error);
            await error;
            return await output;
        }
    }
}
