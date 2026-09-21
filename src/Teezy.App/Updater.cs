using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Teezy.Core.Updates;

namespace Teezy.App;

/// <summary>Where an update has got to. One snapshot, replaced whole on every change.</summary>
/// <param name="Version">The version being fetched or ready, or null when there is none.</param>
public sealed record UpdateState(
    bool Checking = false,
    Version? Version = null,
    bool Downloading = false,
    bool Ready = false,
    string? Error = null)
{
    /// <summary>One line for Settings ▸ About, in Fivebar's words.</summary>
    public string Describe() =>
        Error ?? (Ready ? $"Version {Version} is ready: restart TeezyFlow to finish."
            : Downloading ? $"Getting version {Version}…"
            : Checking ? "Looking for a new version…"
            : "TeezyFlow is up to date.");
}

/// <summary>Keeps TeezyFlow up to date from its GitHub releases, the way Fivebar does.</summary>
/// <remarks>
/// <para>
/// Checks shortly after launch and every four hours, downloads a newer release in the
/// background, and then waits: the window shows a bar with <b>Restart now</b>, and if that is
/// never pressed the update installs when TeezyFlow is next quit. There is no switch to turn
/// it off, as in Fivebar — an app that is always running is exactly the one that never gets
/// updated by hand.
/// </para>
/// <para>
/// <b>A download is only ever run after it matches.</b> GitHub publishes each asset's SHA-256,
/// and the installer is checked against it and against its size before it is kept; anything
/// else is deleted. The file arrives over HTTPS from the repository's own releases, and a file
/// written by this process carries no mark-of-the-web, so there is no SmartScreen prompt on an
/// update the way there is on a first download from a browser.
/// </para>
/// <para>
/// Only the installed copy updates itself. A build run from the repo or from dist\ would
/// otherwise replace the installed app with whatever is published, underneath a developer.
/// </para>
/// </remarks>
public sealed class Updater : IDisposable
{
    private const string LatestUrl = "https://api.github.com/repos/tee-93/teezy/releases/latest";
    private static readonly TimeSpan Every = TimeSpan.FromHours(4);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;
    private readonly Version _running;
    private readonly string _folder;
    private readonly Timer _timer;
    private int _busy;
    private string? _installer;

    public event Action<UpdateState>? Changed;

    public UpdateState State { get; private set; } = new();

    public Updater()
    {
        _running = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teezy", "updates");

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TeezyFlow", _running.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _timer = new Timer(_ => _ = CheckAsync(manual: false), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Whether this process is the installed app, rather than a build run from elsewhere.</summary>
    public static bool IsInstalledCopy
    {
        get
        {
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            return Environment.ProcessPath is { } exe
                   && exe.StartsWith(programs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Starts the schedule: once shortly after launch, then every four hours.</summary>
    /// <remarks>Not at once: launch is when the speech model is loading, and that comes first.</remarks>
    public void Start()
    {
        if (IsInstalledCopy) _timer.Change(FirstCheck, Every);
    }

    /// <summary>Looks for a newer release and, if there is one, fetches it.</summary>
    /// <param name="manual">From the button: say what went wrong rather than "later".</param>
    public async Task CheckAsync(bool manual)
    {
        if (!IsInstalledCopy)
        {
            if (manual) Set(new UpdateState(Error: "Updates only work in the installed app."));
            return;
        }

        if (State.Ready || Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            Set(State with { Checking = true, Error = null });

            var json = await _http.GetStringAsync(LatestUrl).ConfigureAwait(false);
            var release = Release.Parse(json);

            if (release is null || !release.IsNewerThan(_running))
            {
                Set(new UpdateState());
                return;
            }

            Set(new UpdateState(Version: release.Version, Downloading: true));
            _installer = await DownloadAsync(release).ConfigureAwait(false);
            Set(new UpdateState(Version: release.Version, Ready: true));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or InvalidDataException or System.Text.Json.JsonException)
        {
            Set(new UpdateState(Error: manual
                ? "TeezyFlow couldn’t reach the update server. Check the internet connection."
                : "TeezyFlow couldn’t check for updates. It will try again later."));
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Runs the downloaded installer and quits, so it can replace the running exe.
    /// </summary>
    /// <param name="relaunch">
    /// True for Restart now: the installer starts TeezyFlow again when it is done. False when
    /// quitting: someone who chose Quit did not ask for it back.
    /// </param>
    /// <returns>False if there is nothing ready to install.</returns>
    public bool Install(bool relaunch)
    {
        if (!State.Ready || _installer is null || !File.Exists(_installer)) return false;

        // /VERYSILENT: no pages, not even the Ready page Inno shows when every other page is
        // off. /CLOSEAPPLICATIONS: Restart Manager closes this process if it has not finished
        // quitting by the time the installer wants the file. /update=1 is read by the script's
        // [Run] section to start the app again afterwards.
        var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
                        + (relaunch ? " /update=1" : string.Empty);

        Process.Start(new ProcessStartInfo(_installer, arguments) { UseShellExecute = false });
        return true;
    }

    private async Task<string> DownloadAsync(Release release)
    {
        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, $"TeezyFlow-Setup-{release.Version}.exe");

        // Older downloads are of no further use once a newer one is on its way.
        foreach (var old in Directory.EnumerateFiles(_folder).Where(f => !f.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(old);
        }

        // Already fetched on an earlier run, and still intact: no need to fetch it again.
        if (File.Exists(target) && await MatchesAsync(target, release).ConfigureAwait(false)) return target;

        var partial = target + ".part";
        using (var response = await _http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file).ConfigureAwait(false);
        }

        if (!await MatchesAsync(partial, release).ConfigureAwait(false))
        {
            TryDelete(partial);
            throw new InvalidDataException("The downloaded installer did not match the release.");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    private static async Task<bool> MatchesAsync(string path, Release release)
    {
        if (release.Size > 0 && new FileInfo(path).Length != release.Size) return false;
        if (release.Sha256 is null) return true;

        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)).ToLowerInvariant();
        return hash == release.Sha256;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Set(UpdateState next)
    {
        State = next;
        Changed?.Invoke(next);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
