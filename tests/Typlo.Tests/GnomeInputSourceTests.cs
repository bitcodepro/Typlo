using System.Text;
using Typlo.App.Platform.Linux;
using Typlo.App.Platform.Shared;

namespace Typlo.Tests;

public class GnomeInputSourceTests
{
    private static Proc.Result Ok(string text) => new(0, Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("GNOME", "gnome", true)]
    [InlineData("ubuntu:GNOME", "ubuntu", true)]
    [InlineData(null, "gnome-xorg", true)]
    [InlineData("KDE", "plasma", false)]
    [InlineData("openbox", "openbox", false)]
    public void DesktopDetection_DoesNotReadStaleGnomeSettingsOnOtherDesktops(string? desktop, string session, bool expected) =>
        Assert.Equal(expected, GnomeInputSources.IsGnome(desktop, session));

    [Fact]
    public void GnomeX11Discovery_IncludesAllFiveConfiguredSourcesAndTheirVariants()
    {
        if (!OperatingSystem.IsLinux()) return;
        var commands = new List<string>();
        var sources = LinuxKeyboardLayouts.ReadSources(true, (tool, _) =>
        {
            commands.Add(tool);
            return tool == "gsettings" ? Ok("[('xkb', 'us'), ('xkb', 'ru'), ('xkb', 'ua'), ('xkb', 'fr+oss'), ('xkb', 'de')]")
                : Ok("layout: us,ru,ua,us\nvariant: ,,,\n");
        });
        var ids = new List<string>();
        foreach (var source in sources) ids.Add(source.Id);
        Assert.Equal(["xkb:us:", "xkb:ru:", "xkb:ua:", "xkb:fr:oss", "xkb:de:"], ids);
        Assert.Equal(["gsettings"], commands); // The currently loaded subset must not truncate the catalog.
    }

    [Theory]
    [InlineData(false, "[('xkb', 'us'), ('xkb', 'ru')]")]
    [InlineData(true, "@a(ss) []")]
    [InlineData(true, "unavailable")]
    public void Discovery_FallsBackToLoadedXkbWhenDesktopCatalogIsUnavailable(bool gnome, string desktopSources)
    {
        if (!OperatingSystem.IsLinux()) return;
        var sources = LinuxKeyboardLayouts.ReadSources(gnome, (tool, _) => tool == "gsettings"
            ? desktopSources == "unavailable" ? new(-1, []) : Ok(desktopSources)
            : Ok("layout: us,de\nvariant: ,nodeadkeys\n"));
        var ids = new List<string>();
        foreach (var source in sources) ids.Add(source.Id);
        Assert.Equal(["xkb:us:", "xkb:de:nodeadkeys"], ids);
    }

    [Fact]
    public void Bridge_ActivatesTheFifthSourceByIdentity_NotAsXkbGroupFour()
    {
        var calls = new List<string[]>();
        var bridge = new GnomeInputSources((tool, args) =>
        {
            Assert.Equal("gdbus", tool);
            calls.Add(args);
            return args.Contains("io.github.bitcodepro.Typlo.InputSources.GetCurrent") ? Ok("('fr+oss',)\n") : Ok("(true,)\n");
        });
        Assert.True(bridge.Activate("xkb:de:", () => true));
        Assert.Equal("de", calls.Single(c => c.Contains("io.github.bitcodepro.Typlo.InputSources.Activate"))[^1]);
        Assert.Equal("xkb:fr:oss", bridge.Current());
        Assert.True(bridge.Activate("xkb:fr:oss", () => true));
        Assert.Equal("fr+oss", calls.Last()[^1]);
    }

    [Fact]
    public void Bridge_FocusLossDuringAvailabilityProbe_PreventsActivation()
    {
        var focused = true;
        var calls = new List<string[]>();
        var bridge = new GnomeInputSources((_, args) => { calls.Add(args); focused = false; return Ok("(true,)"); });
        Assert.False(bridge.Activate("xkb:de:", () => focused));
        Assert.Single(calls); // Ping only, no activation after the slow probe.
    }

    [Fact]
    public void MissingBridge_IsReportedUnavailable_AndRepeatedChecksDoNotSpawnProcesses()
    {
        var calls = 0;
        var bridge = new GnomeInputSources((_, _) => { calls++; return new(-1, []); });
        Assert.False(bridge.Available);
        Assert.Null(bridge.Current());
        Assert.False(bridge.Activate("xkb:de:", () => true));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("win:00000409")]
    [InlineData("xkb:de:nodeadkeys:unexpected")]
    [InlineData("xkb:de:bad'argument")]
    public void Bridge_RejectsForeignOrMalformedIdentities(string id)
    {
        var bridge = new GnomeInputSources((_, _) => throw new InvalidOperationException("Must not call gdbus"));
        Assert.False(bridge.Activate(id, () => true));
    }
}
