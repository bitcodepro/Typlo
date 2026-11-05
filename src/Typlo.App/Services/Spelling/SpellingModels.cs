using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Typlo.Core.Languages;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed record SpellingRequest(string Text, string Language = "auto", string[]? Ignored = null, SpellingCorrection[]? Corrections = null, string[]? Excluded = null);
public sealed record SpellingIssue(int Start, int Length, string Word, string Language, string[] Suggestions);
public sealed record SpellingResult(SpellingIssue[] Issues, long Milliseconds, long WorkingSet, long PeakWorkingSet, long ManagedBytes);
public sealed record SpellingSelection(long Id, string Text, string[] Ignored, string? Notice = null, bool CanApply = false, string[]? Excluded = null);

[JsonSerializable(typeof(SpellingRequest))]
[JsonSerializable(typeof(SpellingResult))]
internal partial class SpellingJson : JsonSerializerContext;

public static partial class SpellingText
{
    public const int MaxCharacters = 20_000, MaxIssues = 300, MaxWords = 3000;
    public const int MaxSuggestionCharacters = 128, MaxResultCharacters = MaxCharacters + MaxIssues * MaxSuggestionCharacters;
    [GeneratedRegex(@"\S+")]
    private static partial Regex Chunks();
    [GeneratedRegex(@"\p{L}[\p{L}\p{M}]*(?:['’ʼ-][\p{L}\p{M}]+)*")]
    private static partial Regex Words();
    public static IEnumerable<(int Start, string Word)> Tokens(string text)
    {
        var inCode = false;
        foreach (Match chunk in Chunks().Matches(text))
        {
            if (chunk.Value.Contains('`')) { if (chunk.Value.Count(c => c == '`') % 2 != 0) inCode = !inCode; continue; }
            // Only prose wrappers are removed. Keep the original indices for replacement,
            // and preserve empty-argument calls such as Speling() as technical input.
            var prose = chunk.Value.Trim('(', ')', '[', ']', '{', '}', '"', '\'', '«', '»', '“', '”', '‘', '’', '‹', '›', '.', ',', '!', '?', ';', '…');
            if (inCode || chunk.Value.Contains("()", StringComparison.Ordinal) || TechnicalToken.IsProtected(prose)) continue;
            foreach (Match word in Words().Matches(chunk.Value))
                if (word.Length is >= 2 and <= 64) yield return (chunk.Index + word.Index, word.Value);
        }
    }
    public static string Apply(string text, SpellingIssue[] issues, IReadOnlyList<string?> choices)
    {
        if (choices.Count != issues.Length) throw new ArgumentException("Invalid spelling choices");
        var result = new System.Text.StringBuilder(); var offset = 0;
        for (var i = 0; i < issues.Length; i++)
        {
            var issue = issues[i];
            if (issue.Start < offset || issue.Length < 1 || issue.Start > text.Length - issue.Length ||
                text.Substring(issue.Start, issue.Length) != issue.Word) throw new ArgumentException("Invalid spelling range");
            var choice = choices[i];
            if (choice is not null && !issue.Suggestions.Contains(choice)) throw new ArgumentException("Invalid spelling suggestion");
            result.Append(text.AsSpan(offset, issue.Start - offset)); result.Append(choice ?? issue.Word);
            offset = issue.Start + issue.Length;
        }
        return result.Append(text.AsSpan(offset)).ToString();
    }
}
