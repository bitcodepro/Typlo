namespace Typlo.Core.Languages;

/// <summary>Conservative lexical guard, independent of dictionary evidence. Never used for manual conversion.</summary>
public static class TechnicalToken
{
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    { "cmd", "powershell", "powershell_ise", "pwsh", "WindowsTerminal", "WindowsTerminalPreview", "bash", "zsh", "fish", "sh", "Terminal", "gnome-terminal-server", "konsole", "alacritty", "kitty", "wezterm-gui", "iTerm2" };
    public static bool IsShellProcess(string name) => Shells.Contains(Settings.ApplicationLayoutRule.ProcessKey(name));
    public static bool IsProtected(string text)
    {
        var token = text.Trim();
        if (token.Length == 0) return false;
        if (token.Any(c => char.IsDigit(c) || c is '@' or '_' or '\\' or '/' or ':' or '=' or '`' or '$' or '|' or '<' or '>' or '+' or '*' or '&')) return true;
        if (token.StartsWith('-') || token.StartsWith('~') || token.StartsWith('#')) return true;
        if (token.Contains("::") || token.Contains("->") || token.Contains("()") || token.Contains("--")) return true;
        // Keep mixed-case identifiers, while allowing ordinary Title Case and accented language words.
        if (token.Skip(1).Any(char.IsUpper) && token.Any(char.IsLower)) return true;
        var bare = token.Trim('.', ',', '!', '?', ';', '"', '(', ')', '[', ']', '{', '}');
        var parts = bare.Split('.');
        if (parts.Length > 1 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiLetter)))
        {
            var suffix = parts[^1].ToLowerInvariant();
            if (parts[0].Equals("www", StringComparison.OrdinalIgnoreCase) ||
                new[] { "com", "org", "net", "io", "dev", "app", "ua", "uk", "fr", "de", "es", "it", "pt", "json", "xml", "cs", "js", "ts", "py", "txt", "exe", "dll", "md", "sql", "sh", "ps" }.Contains(suffix)) return true;
        }
        return false;
    }
}
