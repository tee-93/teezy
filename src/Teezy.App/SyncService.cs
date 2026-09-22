using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Calendar;
using Teezy.Core.Sync;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>What Settings ▸ Sync shows.</summary>
/// <param name="On">Whether this computer is syncing.</param>
/// <param name="Message">One line: in sync, where the last change came from, or what is wrong.</param>
/// <param name="Problem">True when the message is a problem rather than a status.</param>
public sealed record SyncStatus(bool On, string Message, bool Problem = false);

/// <summary>
/// Keeps keys, settings and the dictionary the same on every computer, through one encrypted
/// file in a folder they all see — usually OneDrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>The newest file wins, whole.</b> Each computer writes the file when something it syncs
/// changes, and applies it when a newer one arrives. Two computers changing different things
/// while both are offline would lose one set of changes — acceptable for one person with three
/// machines, and far simpler to trust than a field-by-field merge.
/// </para>
/// <para>
/// <b>Nothing bounces.</b> A write happens only when what would be written differs from what the
/// file already holds — so applying another computer's file, which changes settings and the
/// dictionary here, does not immediately write the same thing back with a newer time, which the
/// other computer would then apply, and so on for ever.
/// </para>
/// <para>
/// <b>Except tasks, which merge.</b> A task list is edited on every computer, often while another
/// is asleep with older news, so whole-file-wins would lose tasks. Each task carries when it last
/// changed, and the file's tasks are folded into this computer's — newer copy of each wins —
/// whenever the file is read, and again just before it is written, so a write never drops a task
/// another computer added since.
/// </para>
/// <para>
/// Per-computer things never travel: see <see cref="TeezySettings.LocalOnly"/>. Neither do
/// sign-ins — each computer signs in to its own accounts — nor the passphrase itself, which is
/// kept on each computer under DPAPI so it is typed once per machine.
/// </para>
/// </remarks>
public sealed class SyncService : IDisposable
{
    private const string PassphraseName = "sync-passphrase";

    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _apply;
    private readonly ISecretStore _secrets;
    private readonly string _dictionaryPath;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly TaskStore? _tasks;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;
    private FileSystemWatcher? _watcher;
    private string? _lastContent;
    private bool _applying;

    public event Action<SyncStatus>? Changed;

    public SyncStatus Status { get; private set; } = new(false, "Sync is off on this computer.");

    /// <param name="apply">Saves settings the way any other change is saved.</param>
    /// <param name="secretNames">The keys that travel, by the name the store files them under.</param>
    public SyncService(
        Func<TeezySettings> settings,
        Action<TeezySettings> apply,
        ISecretStore secrets,
        string dictionaryPath,
        IReadOnlyList<string> secretNames,
        TaskStore? tasks = null)
    {
        _settings = settings;
        _apply = apply;
        _secrets = secrets;
        _dictionaryPath = dictionaryPath;
        _secretNames = secretNames;
        _tasks = tasks;
        _ui = Dispatcher.CurrentDispatcher;

        // A burst of edits — typing a key, ticking three switches — becomes one write.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Push(); };

        // The watcher is the fast path; this catches what it misses, which a synced cloud folder
        // occasionally does when the file is replaced from outside.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _poll.Tick += (_, _) => Pull();
    }

    public bool IsOn => _settings().SyncFolder is { Length: > 0 } && _secrets.Read(PassphraseName) is { Length: > 0 };

    private string? FilePath => _settings().SyncFolder is { Length: > 0 } folder
        ? Path.Combine(folder, SyncProfile.FileName)
        : null;

    /// <summary>At launch: take anything newer, then start watching.</summary>
    public void Start()
    {
        if (!IsOn) return;
        Pull();
        Push();
        Watch();
    }

    /// <summary>Turns sync on with a folder and passphrase, joining a file that is already there.</summary>
    /// <returns>Where the setup came from, for the confirmation line.</returns>
    /// <exception cref="SyncUnlockException">The folder has a sync file the passphrase does not open.</exception>
    /// <exception cref="IOException">The folder cannot be read or written.</exception>
    public string TurnOn(string folder, string passphrase)
    {
        var path = Path.Combine(folder, SyncProfile.FileName);
        Directory.CreateDirectory(folder);

        SyncProfile? existing = null;
        if (File.Exists(path)) existing = SyncProfile.FromJson(SyncCipher.Open(File.ReadAllText(path), passphrase));

        _secrets.Write(PassphraseName, passphrase);
        _apply(_settings() with { SyncFolder = folder, SyncAppliedAt = null });

        string outcome;
        if (existing is not null)
        {
            Apply(existing);
            outcome = $"Joined. This computer now has the setup from {existing.SavedBy}.";
        }
        else
        {
            Push(force: true);
            outcome = "On. Use the same folder and passphrase on your other computers.";
        }

        Watch();
        Set(new SyncStatus(true, outcome));
        return outcome;
    }

    /// <summary>Stops syncing here. The file, and the other computers, are left alone.</summary>
    public void TurnOff()
    {
        _watcher?.Dispose();
        _watcher = null;
        _poll.Stop();
        _debounce.Stop();
        _secrets.Delete(PassphraseName);
        _apply(_settings() with { SyncFolder = null, SyncAppliedAt = null });
        _lastContent = null;
        Set(new SyncStatus(false, "Sync is off on this computer."));
    }

    /// <summary>Something that travels has changed here: write it out shortly.</summary>
    public void LocalChanged()
    {
        if (_applying || !IsOn) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>A secret was written or deleted; only the ones that travel matter.</summary>
    public void SecretChanged(string name)
    {
        if (name != PassphraseName && Travels(name)) LocalChanged();
    }

    /// <summary>Writes the file now, from the Sync now button.</summary>
    public void SyncNow()
    {
        Pull();
        Push(force: true);
    }

    // ---- reading ----

    private void Pull()
    {
        if (FilePath is not { } path || _secrets.Read(PassphraseName) is not { Length: > 0 } passphrase) return;

        try
        {
            if (!File.Exists(path)) return;

            var profile = SyncProfile.FromJson(SyncCipher.Open(ReadShared(path), passphrase));
            if (_settings().SyncAppliedAt is { } applied && profile.SavedAt <= applied)
            {
                // Nothing newer to apply, but tasks merge regardless of which file is newest.
                if (MergeTasks(profile)) LocalChanged();
                return;
            }

            Apply(profile);
        }
        catch (SyncUnlockException e)
        {
            Set(new SyncStatus(true, $"{e.Message} If the passphrase was changed on another computer, turn sync off here and on again with the new one.", Problem: true));
        }
        catch (IOException)
        {
            // Mid-sync, or the folder is briefly unavailable. The watcher or the poll tries again.
        }
    }

    private void Apply(SyncProfile profile)
    {
        var ahead = MergeTasks(profile);

        _applying = true;
        try
        {
            foreach (var (name, value) in profile.Secrets)
            {
                if (Travels(name) && _secrets.Read(name) != value) _secrets.Write(name, value);
            }

            if (profile.Dictionary is { } dictionary && ReadDictionary() != dictionary)
            {
                File.WriteAllText(_dictionaryPath, dictionary);
            }

            _apply(_settings().WithPortable(profile.Settings) with { SyncAppliedAt = profile.SavedAt });
            // What the file holds now matches this computer — unless this computer had tasks the
            // file lacked, in which case the file needs writing again.
            _lastContent = ahead ? null : Content(Snapshot(profile.SavedAt));

            var local = profile.SavedBy == Environment.MachineName;
            Set(new SyncStatus(true, local
                ? $"In sync. Last saved from this computer {Ago(profile.SavedAt)}."
                : $"In sync. The latest change came from {profile.SavedBy}, {Ago(profile.SavedAt)}."));
        }
        finally
        {
            _applying = false;
        }

        if (ahead) LocalChanged();
    }

    /// <summary>Folds the file's tasks into this computer's.</summary>
    /// <returns>Whether this computer has task news the file lacks, and so should write it.</returns>
    private bool MergeTasks(SyncProfile profile)
    {
        if (_tasks is null) return false;
        if (profile.Tasks is { } json)
        {
            try { _tasks.Merge(TaskStore.FromJson(json)); }
            catch (System.Text.Json.JsonException) { }
        }

        return _tasks.ToSyncJson() != profile.Tasks;
    }

    /// <summary>
    /// Just before writing: takes in any tasks another computer wrote since this one last read, so
    /// the write carries them rather than wiping them.
    /// </summary>
    private void MergeTasksFromFile(string path, string passphrase)
    {
        if (_tasks is null || !File.Exists(path)) return;
        try
        {
            MergeTasks(SyncProfile.FromJson(SyncCipher.Open(ReadShared(path), passphrase)));
        }
        catch (Exception e) when (e is SyncUnlockException or IOException or UnauthorizedAccessException)
        {
            // Unreadable just now; the write below still carries everything this computer has.
        }
    }

    // ---- writing ----

    private void Push(bool force = false)
    {
        if (FilePath is not { } path || _secrets.Read(PassphraseName) is not { Length: > 0 } passphrase) return;

        MergeTasksFromFile(path, passphrase);

        var now = DateTimeOffset.Now;
        var profile = Snapshot(now);
        var content = Content(profile);
        if (!force && content == _lastContent) return;

        try
        {
            // Written beside the real file and swapped in, so the cloud client never uploads —
            // and another computer never reads — half a file.
            var temp = path + ".writing";
            File.WriteAllText(temp, SyncCipher.Seal(profile.ToJson(), passphrase));
            File.Move(temp, path, overwrite: true);

            _lastContent = content;
            _applying = true;
            try { _apply(_settings() with { SyncAppliedAt = now }); }
            finally { _applying = false; }

            Set(new SyncStatus(true, $"In sync. Last saved from this computer {Ago(now)}."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Set(new SyncStatus(true, $"Couldn’t write to the sync folder: {e.Message}", Problem: true));
        }
    }

    private SyncProfile Snapshot(DateTimeOffset at)
    {
        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in TravellingSecretNames())
        {
            if (_secrets.Read(name) is { Length: > 0 } value) secrets[name] = value;
        }

        return new SyncProfile(at, Environment.MachineName, _settings().ToPortable(), secrets, ReadDictionary(),
            _tasks?.ToSyncJson());
    }

    /// <summary>Everything but the time, for "has anything actually changed".</summary>
    private static string Content(SyncProfile profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (profile with { SavedAt = default, SavedBy = string.Empty }).ToJson())));

    // ---- helpers ----

    private IEnumerable<string> TravellingSecretNames() =>
        _secretNames.Concat(_settings().ConnectedAccounts
            .Where(a => a.Source == CalendarSource.Ics)
            .Select(a => $"calendar-link-{a.Id}"));

    private bool Travels(string name) =>
        _secretNames.Contains(name) || name.StartsWith("calendar-link-", StringComparison.Ordinal);

    private string? ReadDictionary()
    {
        try { return File.Exists(_dictionaryPath) ? File.ReadAllText(_dictionaryPath) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>Read while a cloud client may also have it open.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        if (_settings().SyncFolder is not { Length: > 0 } folder || !Directory.Exists(folder)) return;

        _watcher = new FileSystemWatcher(folder, SyncProfile.FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        // Cloud clients often replace the file rather than write it, so renames count too. A
        // short delay lets them finish before it is read.
        void Soon() => _ui.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(1500); Pull(); });
        _watcher.Changed += (_, _) => Soon();
        _watcher.Created += (_, _) => Soon();
        _watcher.Renamed += (_, _) => Soon();

        _poll.Start();
    }

    private static string Ago(DateTimeOffset at)
    {
        var gap = DateTimeOffset.Now - at;
        return gap.TotalMinutes < 1 ? "just now"
            : gap.TotalHours < 1 ? $"{(int)gap.TotalMinutes} min ago"
            : gap.TotalDays < 1 ? $"{(int)gap.TotalHours} h ago"
            : at.LocalDateTime.ToString("d MMM, h:mm tt");
    }

    private void Set(SyncStatus status)
    {
        Status = status;
        Changed?.Invoke(status);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _poll.Stop();
        _debounce.Stop();
    }
}
