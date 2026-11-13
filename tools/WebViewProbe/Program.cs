using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        ApplicationConfiguration.Initialize();
        using var window = new ProbeWindow
        {
            Text = "Typlo isolated WebView2 fixture",
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            ClientSize = new Size(500, 200),
            Opacity = 0
        };
        using var browser = new WebView2 { Dock = DockStyle.Fill };
        window.Controls.Add(browser);
        var exit = 1;
        window.Shown += async (_, _) =>
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--force-renderer-accessibility --disable-gpu"
                };
                var environment = await CoreWebView2Environment.CreateAsync(null, args[1], options);
                await browser.EnsureCoreWebView2Async(environment);
                await CheckAsync(browser, window.Handle, args[0], "textarea",
                    """<textarea aria-label="Typlo test editor" id="editor">🙂 ghbdtn TAIL</textarea>""",
                    """const e = document.getElementById('editor'); e.focus(); e.setSelectionRange(3, 9); e.value.slice(e.selectionStart, e.selectionEnd)""",
                    "🙂 привет TAIL");
                await CheckAsync(browser, window.Handle, args[0], "single-line input",
                    """<input aria-label="Typlo test editor" id="editor" value="🙂 ghbdtn TAIL">""",
                    """const e = document.getElementById('editor'); e.focus(); e.setSelectionRange(3, 9); e.value.slice(e.selectionStart, e.selectionEnd)""",
                    "🙂 привет TAIL");
                await CheckAsync(browser, window.Handle, args[0], "textarea multiline",
                    "<textarea aria-label=\"Typlo test editor\" id=\"editor\">🙂 ghbdtn TAIL\nsecond line</textarea>",
                    """const e = document.getElementById('editor'); e.focus(); e.setSelectionRange(3, 9); e.value.slice(e.selectionStart, e.selectionEnd)""",
                    "🙂 привет TAIL\nsecond line");
                await CheckAsync(browser, window.Handle, args[0], "contenteditable",
                    """<div aria-label="Typlo test editor" role="textbox" contenteditable="true" id="editor">🙂 ghbdtn TAIL</div>""",
                    """const e = document.getElementById('editor'); e.focus(); const r = document.createRange(); r.setStart(e.firstChild, 3); r.setEnd(e.firstChild, 9); const s = getSelection(); s.removeAllRanges(); s.addRange(r); s.toString()""",
                    "🙂 привет TAIL");
                exit = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
            finally
            {
                window.Close();
            }
        };
        Application.Run(window);
        return exit;
    }

    private static async Task CheckTypedSpaceAsync(WebView2 browser, nint window, string executable, string kind)
    {
        var result = await browser.ExecuteScriptAsync("""
            (() => {
                const field = document.getElementById('editor');
                field.focus();
                const typed = 'keep ghbdtn';
                if ('value' in field) {
                    field.value = typed;
                    field.setSelectionRange(typed.length, typed.length);
                } else {
                    field.textContent = typed;
                    getSelection().collapse(field.firstChild, typed.length);
                }
                if (!document.execCommand('insertText', false, ' ')) return null;
                return field.value ?? field.textContent;
            })()
            """);
        var text = System.Text.Json.JsonSerializer.Deserialize<string>(result);
        if (text is null || text.Length != 12 || !text.StartsWith("keep ghbdtn", StringComparison.Ordinal)
            || text[^1] is not (' ' or '\u00a0'))
            throw new InvalidOperationException("Could not type the sample with a trailing space");
        var replacement = "привет" + text[^1];
        await RunAsync(window, executable, kind, "--uia-correction-range", "ghbdtn ", replacement);
        await RunAsync(window, executable, kind, "--uia-range", "12", "12");
        var original = "ghbdtn" + text[^1];

        await ReplaceWithoutPastingSeparatorAsync(original, replacement);
        await ReplaceWithoutPastingSeparatorAsync(replacement, original);
        await RunAsync(window, executable, kind, "--uia-range", "5", "12");
        await ReplaceWithoutPastingSeparatorAsync(original, replacement);
        await ReplaceWithoutPastingSeparatorAsync(replacement, original);
        Console.WriteLine($"PASS {kind}: correction, Undo, selection including separator and Undo preserve U+{(int)text[^1]:X4}");

        async Task ReplaceWithoutPastingSeparatorAsync(string from, string to)
        {
            await RunAsync(window, executable, kind, "--uia-preserve-separator", from, to);
            await RunAsync(window, executable, kind, "--uia-snapshot", from[..^1]);
            var literal = System.Text.Json.JsonSerializer.Serialize(to);
            var edited = await browser.ExecuteScriptAsync($$"""
                (() => {
                    const field = document.getElementById('editor');
                    if (!document.execCommand('insertText', false, {{literal}}.trimEnd())) return null;
                    return field.value ?? field.textContent;
                })()
                """);
            if (System.Text.Json.JsonSerializer.Deserialize<string>(edited) != "keep " + to)
                throw new InvalidOperationException($"Editor did not preserve the prefix and trailing space in {kind}");
            await RunAsync(window, executable, kind, "--uia-range", "12", "12");
            var caret = await browser.ExecuteScriptAsync("""
                (() => {
                    const field = document.getElementById('editor');
                    if ('selectionStart' in field) return [field.selectionStart, field.selectionEnd];
                    const selection = getSelection();
                    if (!selection.isCollapsed) return null;
                    const prefix = document.createRange();
                    prefix.selectNodeContents(field);
                    prefix.setEnd(selection.focusNode, selection.focusOffset);
                    const offset = prefix.toString().length;
                    return [offset, offset];
                })()
                """);
            if (caret != "[12,12]")
                throw new InvalidOperationException($"Editor reported the wrong caret in {kind}: {caret}");
        }
    }

    private static async Task CheckAsync(WebView2 browser, nint window, string executable,
        string kind, string html, string select, string expectedText)
    {
        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigation(object? _, CoreWebView2NavigationCompletedEventArgs e)
            => navigated.TrySetResult(e.IsSuccess);
        browser.NavigationCompleted += OnNavigation;
        try
        {
            browser.NavigateToString("<!doctype html>" + html);
            if (!await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("Local HTML navigation failed");
        }
        finally { browser.NavigationCompleted -= OnNavigation; }
        if (await browser.ExecuteScriptAsync(select) != "\"ghbdtn\"")
            throw new InvalidOperationException($"Fixture did not select its sample in {kind}");
        await RunAsync(window, executable, kind, "--uia-snapshot", "ghbdtn");
        var readSelection = kind != "contenteditable"
            ? "(() => { const field = document.getElementById('editor'); return [field.selectionStart, field.selectionEnd]; })()"
            : "(() => { const s = getSelection(); return [Math.min(s.anchorOffset, s.focusOffset), Math.max(s.anchorOffset, s.focusOffset)]; })()";
        foreach (var (start, end) in new[] { ("0", "2"), ("3", "3"), ("3", "9") })
        {
            await RunAsync(window, executable, kind, "--uia-range", start, end);
            var wanted = $"[{start},{end}]";
            string actual = "";
            for (var attempt = 0; attempt < 25; attempt++)
            {
                actual = await browser.ExecuteScriptAsync(readSelection);
                if (actual == wanted) break;
                await Task.Delay(20);
            }
            if (actual != wanted)
                throw new InvalidOperationException($"DOM selection in {kind} is {actual}, expected {wanted}");
        }
        var edited = await browser.ExecuteScriptAsync("""
            (() => {
                const field = document.getElementById('editor');
                if (!document.execCommand('insertText', false, 'привет')) return null;
                return field.value ?? field.textContent;
            })()
            """);
        var actualText = System.Text.Json.JsonSerializer.Deserialize<string>(edited);
        if (actualText != expectedText)
            throw new InvalidOperationException($"Browser edit did not replace the intended range in {kind}");
        await RunAsync(window, executable, kind, "--uia-snapshot", "");
        await CheckTypedSpaceAsync(browser, window, executable, kind);
    }

    private static async Task RunAsync(nint window, string executable, string kind, string command, params string[] args)
    {
        var errors = new List<string>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                ArgumentList = { command, window.ToString(), "Typlo test editor" }
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start Rust fixture probe");
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw; }
            var detail = await stderr;
            if (child.ExitCode == 0)
            {
                Console.WriteLine($"PASS {kind}: {command} {string.Join(" ", args)}");
                return;
            }
            errors.Add(detail.Trim());
            await Task.Delay(200);
        }
        throw new InvalidOperationException($"Rust rejected {kind}: {string.Join("; ", errors.Distinct())}");
    }
}

internal sealed class ProbeWindow : Form
{
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000;
            return parameters;
        }
    }
}
