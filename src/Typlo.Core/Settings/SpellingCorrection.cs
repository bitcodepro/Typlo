using Typlo.Core.Languages;

namespace Typlo.Core.Settings;

public sealed record SpellingCorrection(string Error, string Correction)
{
    public const int MaxPairs = 1000;
    public static string Key(string value) => LanguageLexicon.Normalize(value.Trim());
    public bool IsValid => Error is not null && Correction is not null && Error.Trim().Length <= 64 && Correction.Trim().Length <= 64 &&
        PersonalLexicon.IsWord(Key(Error)) && PersonalLexicon.IsWord(Key(Correction)) && Key(Error) != Key(Correction);
}
