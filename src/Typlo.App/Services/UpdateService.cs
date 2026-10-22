using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using Typlo.Core.Updates;

namespace Typlo.App.Services;

/// <summary>
/// Checks GitHub Releases for a newer version once, shortly after start (never pops up anything). When one exists,
/// <see cref="Available"/> is set and the tray menu offers it; <see cref="InstallAsync"/> downloads it,
/// verifies the SHA-256, swaps the files and restarts the app.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string DefaultFeed = "https://api.github.com/repos/bitcodepro/Typlo/releases/latest";
    // Shortly after start, so the network is up after logon; no periodic re-checks.
    private static readonly TimeSpan CheckDelay = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly string _feed;
    private readonly string _workDir;
    private Timer? _timer;
    private int _busy;

    public static SemVersion CurrentVersion { get; } =
        SemVersion.TryParse(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new SemVersion(0, 0, 0, "dev");

    /// <summary>The newer release, if one was found.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="Available"/> or the install progress changes.</summary>
    public event Action? Changed;

    /// <summary>0..100 while downloading, null otherwise.</summary>
    public int? Progress { get; private set; }

    public string? LastError { get; private set; }

    public UpdateService(string workDir)
    {
        _workDir = workDir;
        // TYPLO_UPDATE_FEED lets tests point the updater at a local server.
        _feed = Environment.GetEnvironmentVariable("TYPLO_UPDATE_FEED") is { Length: > 0 } custom ? custom : DefaultFeed;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Typlo", CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Local developer builds (0.0.0-dev) do not update themselves unless a test feed is set.</summary>
    public bool Enabled => !CurrentVersion.IsPrerelease || CurrentVersion.Prerelease != "dev" || _feed != DefaultFeed;

    public static string? Rid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null) is { } os
            ? RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => $"{os}-x64",
                Architecture.Arm64 when os != "win" => $"{os}-arm64",
                _ => null,
            }
            : null;

    public void Start()
    {
        if (!Enabled || Rid is null) return;
        _timer = new Timer(_ => _ = CheckAsync(), null, CheckDelay, Timeout.InfiniteTimeSpan);
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(_feed);
            var info = ReleaseFeed.Parse(json, CurrentVersion, Rid!);
            if (info?.Version != Available?.Version)
            {
                Available = info;
                if (info is not null) Log.Info($"Update available: {info.Version}");
                Changed?.Invoke();
            }
            return info;
        }
        catch (Exception e)
        {
            Log.Error("Update check failed", e);
            return Available;
        }
    }

    /// <summary>
    /// Where the app's files live: the folder with the executable, or the folder holding Typlo.app on macOS.
    /// </summary>
    public static string InstallRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var marker = ".app" + Path.DirectorySeparatorChar + "Contents";
            var i = dir.IndexOf(marker, StringComparison.Ordinal);
            return i >= 0 ? Path.GetDirectoryName(dir[..(i + 4)])! : dir;
        }
    }

    /// <summary>Removes files left by the previous update.</summary>
    public static void CleanupAfterUpdate()
    {
        try { UpdateInstaller.CleanupOld(InstallRoot); }
        catch (Exception e) { Log.Error("Update cleanup failed", e); }
    }

    /// <summary>Downloads, verifies and installs the update, then starts the new version. True if the app should exit.</summary>
    public async Task<bool> InstallAsync()
    {
        if (Available is not { } info || Interlocked.Exchange(ref _busy, 1) == 1) return false;
        var work = Path.Combine(_workDir, "update-" + info.Version);
        try
        {
            LastError = null;
            SetProgress(0);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            var archive = Path.Combine(work, info.ArchiveName);
            await DownloadAsync(info.ArchiveUrl, archive);
            var checksum = await _http.GetStringAsync(info.ChecksumUrl);
            if (!UpdateInstaller.VerifyChecksum(archive, checksum, info.ArchiveName))
                throw new InvalidDataException("Checksum mismatch: the download is damaged or was tampered with.");

            var staging = Path.Combine(work, "staging");
            UpdateInstaller.Extract(archive, staging);
            var root = InstallRoot;
            UpdateInstaller.Swap(staging, root);
            Log.Info($"Installed {info.Version} into {root}");

            var exe = Environment.ProcessPath!; // the path now holds the new executable
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--updated");
            psi.ArgumentList.Add("--tray");
            Process.Start(psi);
            try { Directory.Delete(work, true); } catch (IOException) { }
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Update failed", e);
            LastError = e.Message;
            return false;
        }
        finally
        {
            Progress = null;
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    private async Task DownloadAsync(string url, string path)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0) SetProgress((int)(read * 100 / total));
        }
    }

    private void SetProgress(int value)
    {
        if (Progress == value) return;
        Progress = value;
        Changed?.Invoke();
    }

    public void OpenReleasePage()
    {
        var url = Available?.PageUrl is { Length: > 0 } page ? page : "https://github.com/bitcodepro/Typlo/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("Cannot open browser", e); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
