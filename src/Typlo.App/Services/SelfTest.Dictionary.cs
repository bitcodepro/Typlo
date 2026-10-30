using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Typlo.App.Views;
using Typlo.Core.Languages;

namespace Typlo.App.Services;

internal static partial class SelfTest
{
    private static async Task<string?> CheckDictionaryCollectionSwitchesAsync(AppController controller)
    {
        // Identical text in both collections reproduces the destructive cross-collection case.
        const string word = "sql";
        await controller.SetPersonalWordAsync(word, "en"); await controller.SetExcludedWordAsync(word);
        DictionaryWindow? window = null;
        var blocked = false;
        var requests = new List<(PersonalWordQuery Query, TaskCompletionSource<PersonalWordPage> Reply)>();
        Task<PersonalWordPage> Query(PersonalWordQuery query)
        {
            if (!blocked) return controller.QueryPersonalWordsAsync(query);
            var reply = new TaskCompletionSource<PersonalWordPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add((query, reply)); return reply.Task;
        }
        T Control<T>(string name) where T : Control
        {
            var matches = window!.GetLogicalDescendants().OfType<T>().Where(c => c.Name == name).Distinct().ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Expected one control {name}, found {matches.Length}");
            return matches[0];
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task UntilUi(Func<bool> condition, string message)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (await Dispatcher.UIThread.InvokeAsync(condition)) return;
                await Task.Delay(10);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException(message);
        }
        bool PageReady() => window!.DisplayedPage is { Total: 1 } && Control<Button>("history_ctx_delete").IsEffectivelyEnabled;
        void AssertBlocked(PersonalWord stale)
        {
            var rows = Control<ListBox>("DictionaryWords");
            var delete = Control<Button>("history_ctx_delete"); var edit = Control<Button>("manage_edit");
            Require(window!.DisplayedPage is null && rows.ItemCount == 0 && rows.SelectedItem is null, "Old rows/selection survive invalidation");
            Require(!rows.IsEffectivelyEnabled && !delete.IsEffectivelyEnabled && !edit.IsEffectivelyEnabled, "Stale row actions are enabled");
            // A queued routed click must also be rejected by the handler, not only visually disabled.
            rows.SelectedItem = stale;
            Control<TextBox>("DictionaryForms").Text = "changedbyrace";
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Control<ComboBox>("DictionaryCollection").IsEnabled, "An invalid click entered a mutation");
        }
        async Task AssertDataIntact()
        {
            var personal = await controller.QueryPersonalWordsAsync(new());
            var exclusions = await controller.QueryPersonalWordsAsync(new(Exclusions: true));
            Require(personal.Items.SequenceEqual(new[] { new PersonalWord(word, "en") }) &&
                exclusions.Items.SequenceEqual(new[] { new PersonalWord(word, "") }), "A blocked action mutated a collection");
        }
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { window = new(controller, queryWords: Query); window.Show(); });
            await UntilUi(PageReady, "Initial dictionary page did not load");
            foreach (var source in new[] { false, true })
            foreach (var fail in new[] { false, true })
            {
                await Dispatcher.UIThread.InvokeAsync(() => { blocked = false; Control<ComboBox>("DictionaryCollection").SelectedIndex = source ? 1 : 0; });
                await UntilUi(PageReady, "Source page did not load");
                var loading = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
                {
                    var rows = Control<ListBox>("DictionaryWords"); rows.SelectedIndex = 0;
                    var stale = (PersonalWord)rows.SelectedItem!;
                    blocked = true; Control<ComboBox>("DictionaryCollection").SelectedIndex = source ? 0 : 1;
                    AssertBlocked(stale); // Runs before any query completion or dispatcher turn.
                    return window!.WordLoadTask;
                });
                await AssertDataIntact();
                var pending = await Dispatcher.UIThread.InvokeAsync(() => requests[^1]);
                if (fail)
                {
                    pending.Reply.SetException(new IOException("fixture dictionary query failed"));
                    await loading;
                    Require(await Dispatcher.UIThread.InvokeAsync(() => Control<TextBlock>("DictionaryMessage").Text == "fixture dictionary query failed"), "Query error was not handled");
                    await Dispatcher.UIThread.InvokeAsync(() => AssertBlocked(new(word, source ? "" : "en")));
                    await AssertDataIntact();
                    await Dispatcher.UIThread.InvokeAsync(() => { blocked = false; Control<Button>("manage_refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                }
                else { pending.Reply.SetResult(await controller.QueryPersonalWordsAsync(pending.Query)); await loading; }
                await UntilUi(PageReady, "Matching page did not restore actions");
                await AssertDataIntact();
            }

            // Rapid switches: a late page from another collection must not restore stale rows.
            var rapidLoads = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                blocked = true; Control<ComboBox>("DictionaryCollection").SelectedIndex = 1;
                var old = window!.WordLoadTask;
                Control<ComboBox>("DictionaryCollection").SelectedIndex = 0;
                return (Old: old, Latest: window.WordLoadTask);
            });
            var latest = await Dispatcher.UIThread.InvokeAsync(() => requests.TakeLast(2).ToArray());
            latest[1].Reply.SetResult(await controller.QueryPersonalWordsAsync(latest[1].Query));
            await rapidLoads.Latest;
            await UntilUi(PageReady, "Latest page did not load");
            latest[0].Reply.SetResult(await controller.QueryPersonalWordsAsync(latest[0].Query));
            await rapidLoads.Old;
            await UntilUi(() => window!.DisplayedPage?.Items.Single().Language == "en", "Old page replaced the latest collection");
            await AssertDataIntact();

            // Once a matching page is accepted, real mutations must work in that collection only.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                blocked = false; Control<ListBox>("DictionaryWords").SelectedIndex = 0;
                Control<Button>("history_ctx_delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await UntilUi(() => window!.DisplayedPage is { Total: 0 } && Control<ComboBox>("DictionaryCollection").IsEnabled,
                "Valid personal deletion did not finish");
            Require((await controller.QueryPersonalWordsAsync(new())).Total == 0 &&
                (await controller.QueryPersonalWordsAsync(new(Exclusions: true))).Total == 1, "Valid deletion touched another collection");
            await Dispatcher.UIThread.InvokeAsync(() => Control<ComboBox>("DictionaryCollection").SelectedIndex = 1);
            await UntilUi(PageReady, "Exclusion page did not load after recovery");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Control<ListBox>("DictionaryWords").SelectedIndex = 0; Control<TextBox>("DictionaryForms").Text = "editedterm";
                Control<Button>("manage_edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await UntilUi(() => window!.DisplayedPage?.Items.SingleOrDefault()?.Word == "editedterm" && Control<ComboBox>("DictionaryCollection").IsEnabled,
                "Valid exclusion edit did not finish");
            Require((await controller.QueryPersonalWordsAsync(new())).Total == 0 &&
                (await controller.QueryPersonalWordsAsync(new(Exclusions: true))).Items.Single().Word == "editedterm", "Valid edit touched another collection");
            return null;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { window?.Close(); foreach (var request in requests) request.Reply.TrySetCanceled(); });
            await controller.SetPersonalWordAsync(word, "en", remove: true); await controller.SetExcludedWordAsync(word, remove: true);
            await controller.SetExcludedWordAsync("editedterm", remove: true);
        }
    }
}
