using System.Diagnostics;
using System.Text.Json;
using Typlo.App.Services;

if (args.Length != 1) throw new ArgumentException("Usage: SpellingProbe path-to-Typlo.exe-or-dll");
using var host = Process.GetCurrentProcess();
static object Memory(Process process)
{
    process.Refresh();
    return new { WorkingSet = SpellingEngine.Metric(() => process.WorkingSet64), PrivateBytes = SpellingEngine.Metric(() => process.PrivateMemorySize64), ManagedBytes = GC.GetTotalMemory(false) };
}
// Warm the client/JSON path before the idle baseline; never start Avalonia or install input hooks.
_ = JsonSerializer.Serialize(new SpellingRequest("warmup"), SpellingJson.Default.SpellingRequest);
GC.Collect(); GC.WaitForPendingFinalizers();
var before = Memory(host);
var samples = new List<object>();
foreach (var (language, text, expected) in new[] { ("en", "speling", "spelling"), ("ru", "превет", "привет"), ("uk", "привітт", "привіт"),
    ("auto", "speling превет привітт", "spelling"), ("auto", "speling превет привітт", "spelling") })
{
    var watch = Stopwatch.StartNew();
    var result = await SpellingWorker.CheckAsync(new(text, language), CancellationToken.None, Path.GetFullPath(args[0]));
    if (!result.Issues.Any(i => i.Suggestions.Contains(expected))) throw new InvalidOperationException("Missing spelling suggestion for " + language);
    samples.Add(new { language, EndToEndMilliseconds = watch.ElapsedMilliseconds, result.Milliseconds, result.WorkingSet, result.PeakWorkingSet, result.ManagedBytes });
}
var after = Memory(host);
var custom = await SpellingWorker.CheckAsync(new("превет", Corrections: [new("превет", "привет")]), CancellationToken.None, Path.GetFullPath(args[0]));
if (custom.Issues.Length != 1 || custom.Issues[0].Suggestions[0] != "привет") throw new InvalidOperationException("Custom correction roundtrip failed");
var excluded = await SpellingWorker.CheckAsync(new("превет", Corrections: [new("превет", "привет")], Excluded: ["превет"]), CancellationToken.None, Path.GetFullPath(args[0]));
if (excluded.Issues.Length != 0) throw new InvalidOperationException("Exclusion did not override a custom correction");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var afterCollection = Memory(host);
// Cancel an active cold load and independently confirm that its process really exited.
using (var cancelActive = new CancellationTokenSource())
{
    Process? child = null;
    try
    {
        try
        {
            await SpellingWorker.CheckAsync(new("превет", "ru"), cancelActive.Token, Path.GetFullPath(args[0]), pid =>
            { child = Process.GetProcessById(pid); _ = child.SafeHandle; cancelActive.Cancel(); });
            throw new InvalidOperationException("Active worker cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        if (child is null || !child.HasExited) throw new InvalidOperationException("Canceled spelling worker survived");
    }
    finally { child?.Dispose(); }
}
// An already-canceled request must not spawn a helper or touch source text.
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { await SpellingWorker.CheckAsync(new("speling"), canceled.Token, Path.GetFullPath(args[0])); throw new InvalidOperationException("Cancellation was ignored"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine(JsonSerializer.Serialize(new { Before = before, After = after, AfterCollection = afterCollection, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
