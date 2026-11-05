using Typlo.Core.Settings;
using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    public async Task<SpellingCorrection[]> GetSpellingCorrectionsAsync()
    {
        if (_remote is not null) UpdateRemoteState(await _remote.CallAsync(new("state")));
        return PublishedSettings.SpellingCorrections.ToArray();
    }
    public async Task SetSpellingCorrectionAsync(string error, string correction, string? original = null, bool remove = false)
    {
        if (_remote is not null)
        {
            UpdateRemoteState(await _remote.CallAsync(new("spelling-pair", Text: error, Args: [correction, original ?? ""], Flag: remove)));
            return;
        }
        lock (_settingsLock)
        {
            EnsureConfigurationWritable();
            var pair = new SpellingCorrection(error, correction);
            if (!remove && !pair.IsValid || remove && !PersonalLexicon.IsWord(SpellingCorrection.Key(error))) throw new ArgumentException(Loc.T("spell.pairInvalid"));
            var key = SpellingCorrection.Key(error); var oldKey = SpellingCorrection.Key(original ?? error);
            var settings = Settings.Clone(); var pairs = settings.SpellingCorrections;
            if (original is not null && !pairs.Any(p => p.Error == oldKey)) throw new ArgumentException(Loc.T("spell.pairStale"));
            if (!remove && pairs.Any(p => p.Error == key && (original is null || key != oldKey))) throw new ArgumentException(Loc.T("spell.pairDuplicate"));
            pairs.RemoveAll(p => p.Error == oldKey);
            if (!remove) pairs.Add(new(key, correction.Trim()));
            if (pairs.Count > SpellingCorrection.MaxPairs) throw new ArgumentException(Loc.T("spell.pairLimit"));
            ApplySettingsCore(settings);
        }
    }
}
