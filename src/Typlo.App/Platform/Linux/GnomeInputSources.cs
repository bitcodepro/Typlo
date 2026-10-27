using System.Text.RegularExpressions;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Platform.Linux;

/// <summary>Small, restricted Shell bridge: GNOME itself activates the source and loads its XKB group.</summary>
internal sealed partial class GnomeInputSources
{
    private const string Interface = "io.github.bitcodepro.Typlo.InputSources";
    private readonly Func<string, string[], Proc.Result> _run;
    private readonly Lock _gate = new();
    private long _lastProbe;
    private bool _probed, _available;

    public GnomeInputSources(Func<string, string[], Proc.Result>? run = null) => _run = run ?? Run;

    internal static bool IsSession => IsGnome(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
        Environment.GetEnvironmentVariable("DESKTOP_SESSION"));

    internal static bool IsGnome(string? desktop, string? session) =>
        (desktop ?? "").Split(':').Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase)) ||
        (session ?? "").StartsWith("gnome", StringComparison.OrdinalIgnoreCase);

    internal static Proc.Result Run(string file, string[] args) => Proc.Has(file)
        ? Proc.Run(file, args, timeoutMs: 1000) : new(-1, []);

    private Proc.Result Call(string method, params string[] args) => _run("gdbus",
        ["call", "--session", "--dest", "org.gnome.Shell", "--object-path", "/io/github/bitcodepro/Typlo/InputSources",
         "--method", Interface + "." + method, .. args]);

    public bool Available
    {
        get
        {
            lock (_gate)
            {
                if (!_probed || Environment.TickCount64 - _lastProbe >= 2000)
                {
                    _available = Call("Ping") is { Ok: true } result && result.Text.Trim() == "(true,)";
                    _lastProbe = Environment.TickCount64;
                    _probed = true;
                }
                return _available;
            }
        }
    }

    public string? Current()
    {
        if (!Available) return null;
        var result = Call("GetCurrent");
        var match = result.Ok ? CurrentSource().Match(result.Text.Trim()) : Match.Empty;
        return match.Success ? NativeId(match.Groups[1].Value) : null;
    }

    public bool Activate(string nativeId, Func<bool> stillFocused)
    {
        var parts = nativeId.Split(':');
        if (parts is not ["xkb", var layout, var variant] || !SourceId().IsMatch(layout) ||
            (variant.Length > 0 && !SourceId().IsMatch(variant))) return false;
        if (!Available) return false;
        // Discovery/probing can take time; check the target immediately before sending the request.
        if (!stillFocused()) return false;
        var result = Call("Activate", variant.Length == 0 ? layout : layout + "+" + variant);
        return result.Ok && result.Text.Trim() == "(true,)";
    }

    internal static string NativeId(string id)
    {
        var parts = id.Split('+', 2);
        return $"xkb:{parts[0]}:{(parts.Length > 1 ? parts[1] : "")}";
    }

    [GeneratedRegex(@"^\('([A-Za-z0-9_-]+(?:\+[A-Za-z0-9_-]+)?)',\)$")]
    private static partial Regex CurrentSource();
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex SourceId();
}
