using Teezy.Core.Hotkeys;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Teezy.Core.Abstractions;

namespace Teezy.Core;

/// <summary>User settings, persisted as JSON next to the model and dictionary.</summary>
public sealed record TeezySettings
{
    /// <summary>The push-to-talk combination. Every key must be held together.</summary>
    public Hotkey Hotkey { get; init; } = Hotkey.Default;

    /// <summary>
    /// The single key this used to be, read only so old settings files still work.
    /// </summary>
    /// <remarks>
    /// Migrated in <see cref="Load"/> and then dropped: it is never written back, so the
    /// file converts itself the first time settings are saved. Kept rather than ignored
    /// because silently resetting someone's hotkey to the default would be worse than any
    /// amount of migration code.
    /// </remarks>
    [JsonPropertyName("PushToTalkKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPushToTalkKey { get; init; }

    /// <summary>
    /// Which microphone to record from. Null follows whatever Windows has chosen.
    /// </summary>
    /// <remarks>
    /// Null is the right default and stays the right answer for most people: Windows already
    /// knows which microphone is in use, and it re-decides when a headset is plugged in. This
    /// exists for the case Windows gets wrong — a laptop that keeps choosing its far-field
    /// array over the headset you are actually speaking into, which does not fail, it just
    /// transcribes badly.
    /// </remarks>
    public string? InputDeviceId { get; init; }

    /// <summary>The name that device had when it was chosen, so Settings can name it.</summary>
    /// <remarks>
    /// Kept only so a device that is currently unplugged can be described as itself rather
    /// than as an opaque endpoint id. Never used to select anything — the id does that.
    /// </remarks>
    public string? InputDeviceName { get; init; }

    /// <summary>Run the deterministic cleanup pass. The dictionary runs either way.</summary>
    public bool CleanupEnabled { get; init; } = true;

    /// <summary>Play a short tone when recording starts and stops.</summary>
    public bool SoundEnabled { get; init; } = true;

    /// <summary>Show the floating level meter while recording.</summary>
    public bool ShowHud { get; init; } = true;

    /// <summary>
    /// Inference threads. Four measured fastest on this hardware; eight measured
    /// <i>slower</i>, so this is a tuned value rather than "more is better".
    /// </summary>
    public int NumThreads { get; init; } = 4;

    /// <summary>Override the model directory. Null means the default location.</summary>
    public string? ModelPath { get; init; }

    /// <summary>How the recogniser decodes. Takes effect when the model is next loaded.</summary>
    /// <remarks>
    /// Greedy by default: it is faster, and it is what every measurement in the README was
    /// taken against. Beam search is the setting to reach for when the engine keeps missing
    /// unusual words — and it is required for dictionary hints to do anything at all.
    /// </remarks>
    public Abstractions.DecodingMethod Decoding { get; init; } = Abstractions.DecodingMethod.Greedy;

    /// <summary>Candidates beam search keeps alive. Ignored under greedy decoding.</summary>
    public int BeamSize { get; init; } = 4;

    /// <summary>How hard dictionary hints pull the recogniser toward their spelling.</summary>
    public double HotwordScore { get; init; } = 1.5;

    /// <summary>Send the cleaned text to Claude for a second, smarter pass.</summary>
    /// <remarks>
    /// Off by default, and off is the honest default: switching it on ends the guarantee that
    /// nothing leaves the machine, needs a paid API account, and adds a network round trip to
    /// every utterance. The API key is <b>not</b> stored here - see <c>ISecretStore</c>.
    /// </remarks>
    public bool LlmCleanupEnabled { get; init; }

    /// <summary>Which Claude model does the cleanup pass.</summary>
    public string LlmModel { get; init; } = "claude-sonnet-5";

    /// <summary>How long to wait before giving up and typing the offline text.</summary>
    public int LlmTimeoutSeconds { get; init; } = 6;

    /// <summary>How much licence the cleanup pass has to change your wording.</summary>
    /// <remarks>
    /// Claude tier only — the offline rules are not tunable. Faithful by default, because a
    /// dictation tool that quietly rephrases you is a worse default than one that leaves an
    /// awkward sentence awkward.
    /// </remarks>
    public Formatting.WritingStyle WritingStyle { get; init; } = Formatting.WritingStyle.Faithful;

    /// <summary>
    /// One extra instruction of your own, appended to whichever style is selected.
    /// </summary>
    /// <remarks>
    /// For the things a preset cannot know: "British spelling", "never use em dashes", "I
    /// write commit messages, keep them imperative". Kept short deliberately — this rides on
    /// every request, and a paragraph here costs tokens on every utterance.
    /// </remarks>
    public string? StyleInstruction { get; init; }

    /// <summary>Styles that apply only in particular apps, checked before the global one.</summary>
    public IReadOnlyList<AppRule> AppRules { get; init; } = [];

    /// <summary>
    /// The style to clean this utterance with, given where it is going.
    /// </summary>
    /// <remarks>
    /// First enabled match wins, so the list is read top to bottom and a rule can be shadowed
    /// by one above it — which is the behaviour the settings page shows, in the order it shows
    /// it. A rule replaces the global instruction rather than adding to it: two instructions
    /// arriving together is how you get contradictory ones.
    /// </remarks>
    public Formatting.CleanupStyle StyleFor(string? app)
    {
        foreach (var rule in AppRules)
        {
            if (rule.Enabled && rule.Matches(app)) return new(rule.Style, rule.Instruction);
        }

        return new(WritingStyle, StyleInstruction);
    }

    /// <summary>
    /// Every combination that is bound to something, keyed by what it does.
    /// </summary>
    /// <remarks>
    /// The single place that knows which setting drives which action. A second voice mode adds
    /// its hotkey here and nowhere else — <see cref="VoiceSession"/> binds whatever it is given
    /// and has no opinion about where the keys came from.
    /// </remarks>
    public IReadOnlyDictionary<Hotkeys.HotkeyAction, Hotkeys.Hotkey> Bindings()
    {
        var bindings = new Dictionary<Hotkeys.HotkeyAction, Hotkeys.Hotkey>
        {
            [Hotkeys.HotkeyAction.Dictate] = Hotkey,
        };

        // Unset by default, and left out entirely rather than bound to nothing: an empty
        // combination watched by the hook would be a key that does nothing, forever.
        if (!AssistantHotkey.IsEmpty)
        {
            bindings[Hotkeys.HotkeyAction.Assistant] = AssistantHotkey;
        }

        return bindings;
    }

    /// <summary>
    /// The combination that speaks to the assistant. Empty means the assistant is off.
    /// </summary>
    /// <remarks>
    /// No default, deliberately. A second global hotkey appearing on upgrade would take a key
    /// combination out of someone's hands without being asked, and the whole feature is opt-in
    /// until it has earned otherwise.
    /// <para>
    /// Worth avoiding a combination that contains <see cref="Hotkey"/>: holding Ctrl+Alt+Win
    /// satisfies Alt+Win on the way, so dictation starts for a few milliseconds first. See
    /// <see cref="Hotkeys.HotkeyBindings"/>.
    /// </para>
    /// </remarks>
    public Hotkeys.Hotkey AssistantHotkey { get; init; } = new();

    /// <summary>
    /// Let Claude interpret commands the local patterns did not recognise.
    /// </summary>
    /// <remarks>
    /// Off by default, and off is the honest default for the same reasons as the cleanup tier:
    /// switching it on ends the guarantee that nothing leaves the machine, needs a paid API
    /// account, and adds a network round trip. The everyday vocabulary stays local either way —
    /// only what the patterns decline is ever sent.
    /// </remarks>
    public bool AssistantLlmEnabled { get; init; }

    /// <summary>Which Claude model interprets what the patterns could not place.</summary>
    /// <remarks>
    /// Haiku by default, unlike the cleanup tier's Sonnet. The job is picking one item off a
    /// list of six or answering in a sentence, which the cheapest model does well — and this
    /// one runs on a key press rather than on every utterance, so latency shows.
    /// </remarks>
    public string AssistantModel { get; init; } = "claude-haiku-4-5";

    /// <summary>How long to wait before giving up and saying so.</summary>
    /// <remarks>
    /// Shorter than the cleanup tier's. Cleanup has offline text to fall back on, so waiting
    /// costs only time; here there is nothing behind it, and someone standing in front of a
    /// pill that says "Thinking" runs out of patience a good deal sooner.
    /// </remarks>
    public int AssistantTimeoutSeconds { get; init; } = 8;

    /// <summary>
    /// Read answers out loud as well as showing them.
    /// </summary>
    /// <remarks>
    /// Answers only, never confirmations. "Volume set to forty percent" takes two seconds to
    /// say for something the pill shows instantly and silently, and you would hear it twenty
    /// times a day; an answer to a question is genuinely better heard than read.
    /// <para>
    /// Off by default, because a computer that starts talking without being asked is a
    /// surprise, and because speech is the kind of thing that is delightful once and
    /// intolerable in an open-plan office.
    /// </para>
    /// </remarks>
    public bool SpeakAnswers { get; init; }

    /// <summary>Which tier reads the answers.</summary>
    /// <remarks>
    /// Windows by default: free, offline, instant, and needs nothing configured. ElevenLabs is
    /// better to listen to and costs a subscription, a third API key, and a round trip before
    /// the first word — which lands on top of the wait for Claude that already happened.
    /// </remarks>
    public VoiceProvider VoiceProvider { get; init; } = VoiceProvider.Windows;

    /// <summary>The ElevenLabs voice id. Unset means that tier is not ready.</summary>
    public string? ElevenLabsVoice { get; init; }

    /// <summary>The Kokoro voice, e.g. <c>bf_emma</c>. Null picks the default British one.</summary>
    public string? KokoroVoice { get; init; }

    /// <summary>The categories a task can have, in the order the picker shows them.</summary>
    /// <remarks>Managed in Settings ▸ Tasks. Travels with sync, so every computer offers the same list.</remarks>
    public IReadOnlyList<string> TaskCategories { get; init; } = [];

    /// <summary>The name on notes written here. Null uses the first part of the Windows account name.</summary>
    public string? TaskAuthor { get; init; }

    /// <summary>Who a note written on this computer is by.</summary>
    [JsonIgnore]
    public string NoteAuthor => TaskAuthor is { Length: > 0 } name ? name.Trim() : FirstName(Environment.UserName);

    /// <summary>"ada.lovelace", "ada_" and "ada-l" are all Ada.</summary>
    public static string FirstName(string? account)
    {
        var cut = (account ?? string.Empty).Split('.', '_', ' ', '-')[0];
        return cut.Length switch
        {
            0 => "Me",
            1 => cut.ToUpperInvariant(),
            _ => char.ToUpperInvariant(cut[0]) + cut[1..],
        };
    }

    /// <summary>
    /// Which ElevenLabs model synthesises.
    /// </summary>
    /// <remarks>
    /// Flash by default. It bills at roughly half the character rate of the higher-quality
    /// models and starts speaking noticeably sooner, and both of those matter more here than
    /// the last few percent of fidelity — this is one or two sentences over a taskbar.
    /// </remarks>
    public string ElevenLabsModel { get; init; } = "eleven_flash_v2_5";

    /// <summary>Which installed voice reads them. Null picks the best available.</summary>
    /// <remarks>
    /// Automatic is the default because the <i>system</i> default is usually the oldest voice
    /// on the machine — choosing beats inheriting that. This is for when you disagree, which is
    /// a matter of taste and not something to argue with.
    /// </remarks>
    public string? SpeechVoice { get; init; }

    /// <summary>
    /// The Entra application id Teezy signs in to Microsoft with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configurable rather than compiled in, because Teezy has no registration of its own yet
    /// and each user brings theirs. Not a secret: a client id identifies the application, it
    /// does not authorise anything, which is why it sits here in plain text while the tokens it
    /// yields go into the encrypted store.
    /// </para>
    /// <para>
    /// When Teezy does ship a registration this becomes an override for people who would rather
    /// use their own, and nothing about the shape has to change.
    /// </para>
    /// </remarks>
    public string? MicrosoftClientId { get; init; }

    /// <summary>The Google Cloud OAuth client Teezy signs in to Google with.</summary>
    /// <remarks>
    /// A Desktop client. Not a secret, for the same reason as the Microsoft one — it identifies
    /// the application and authorises nothing. Its companion secret is not kept here: Google
    /// issues one even for desktop clients, where it cannot actually be kept secret, but it
    /// still does not belong in a plain-text file, so it lives in the encrypted store.
    /// </remarks>
    public string? GoogleClientId { get; init; }

    /// <summary>The accounts that have been signed in to.</summary>
    /// <remarks>
    /// <para>
    /// Only which accounts exist and what to call them. Every credential lives in the secret
    /// store, filed under each account's id — so this file can be read, copied or pasted into a
    /// bug report without handing anyone a calendar or a mailbox.
    /// </para>
    /// <para>
    /// Still written as <c>CalendarAccounts</c> in the file. The name was accurate when only
    /// the diary used it and is no longer, but renaming the stored key would silently disconnect
    /// anyone who had already signed in — a worse outcome than an out-of-date word on disk.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonPropertyName(AccountsJsonName)]
    public IReadOnlyList<Calendar.ConnectedAccount> ConnectedAccounts { get; init; } = [];

    /// <summary>Dashboard widgets shown at their larger size, by name.</summary>
    /// <remarks>
    /// The enlarged ones rather than the normal ones, so a widget added in a later version starts
    /// at its normal size instead of inheriting a preference nobody set.
    /// </remarks>
    public IReadOnlyList<string> ExpandedSections { get; init; } = [];

    /// <summary>Whether the assistant may read mail as well as the diary.</summary>
    /// <remarks>
    /// <para>
    /// Off by default, and a separate switch from connecting the account, because reading
    /// someone's mail is a materially bigger step than reading their diary and should not be a
    /// side effect of wanting to know what is on this afternoon.
    /// </para>
    /// <para>
    /// This does not replace the permission — Microsoft still has to have granted
    /// <c>Mail.Read</c>. It is the local half: a switch the user can see and turn off without
    /// revoking anything.
    /// </para>
    /// </remarks>
    public bool ReadMailEnabled { get; init; }

    /// <summary>The Gmail address to read over IMAP, if any.</summary>
    /// <remarks>
    /// <para>
    /// Gmail arrives over IMAP with an app password rather than through OAuth, because Google
    /// classes its read scope as <i>restricted</i> and the API route therefore needs
    /// verification. The app password lives in the encrypted store; only the address is here.
    /// </para>
    /// <para>
    /// Which means a Gmail account does not appear in <see cref="ConnectedAccounts"/> — that
    /// list is accounts signed in through a provider's own page, and this one was not.
    /// Pretending otherwise would put a row next to the others with a Disconnect button that
    /// meant something different.
    /// </para>
    /// </remarks>
    public string? GmailAddress { get; init; }

    /// <summary>Copy the transcript to the clipboard in addition to typing it.</summary>
    public bool AlsoCopyToClipboard { get; init; }

    /// <summary>
    /// Ignore holds shorter than this. Guards against a tap of the key firing a pointless
    /// record-and-transcribe cycle.
    /// </summary>
    public int MinimumHoldMilliseconds { get; init; } = 200;

    /// <summary>
    /// The folder holding the sync file, on this computer. Null when sync is off.
    /// </summary>
    /// <remarks>
    /// Local to each machine, like everything in <see cref="LocalOnly"/>: the same OneDrive
    /// folder has a different path on every computer it syncs to.
    /// </remarks>
    public string? SyncFolder { get; init; }

    /// <summary>The saved-at time of the last sync file this computer applied or wrote.</summary>
    /// <remarks>What stops a computer re-applying its own write, or an older file over a newer one.</remarks>
    public DateTimeOffset? SyncAppliedAt { get; init; }

    /// <summary>The name accounts are saved under — older than the property's own name.</summary>
    private const string AccountsJsonName = "CalendarAccounts";

    /// <summary>
    /// Settings that belong to this computer rather than to the person, and never travel.
    /// </summary>
    /// <remarks>
    /// The microphone and its name, the thread count tuned to this CPU, where this machine keeps
    /// the model, and the sync plumbing itself. Signed-in accounts are handled separately in
    /// <see cref="WithPortable"/>: each computer signs in for itself, but a calendar link needs
    /// no sign-in and travels.
    /// </remarks>
    public static readonly IReadOnlySet<string> LocalOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(InputDeviceId), nameof(InputDeviceName), nameof(NumThreads), nameof(ModelPath),
        "PushToTalkKey", nameof(SyncFolder), nameof(SyncAppliedAt),
    };

    /// <summary>Everything that should be the same on every computer, as JSON.</summary>
    public JsonObject ToPortable()
    {
        var node = JsonSerializer.SerializeToNode(this, Json)!.AsObject();
        foreach (var name in LocalOnly) node.Remove(name);

        node[AccountsJsonName] = JsonSerializer.SerializeToNode(
            ConnectedAccounts.Where(a => a.Source == Calendar.CalendarSource.Ics).ToList(), Json);
        return node;
    }

    /// <summary>These settings with another computer's portable ones laid over them.</summary>
    /// <remarks>
    /// Local-only settings are kept as they are. Accounts are merged: this computer's own
    /// sign-ins stay, and the calendar links come from the other computer.
    /// </remarks>
    public TeezySettings WithPortable(JsonObject portable)
    {
        var node = JsonSerializer.SerializeToNode(this, Json)!.AsObject();

        foreach (var (name, value) in portable)
        {
            if (LocalOnly.Contains(name) || name == AccountsJsonName) continue;
            node[name] = value?.DeepClone();
        }

        var links = portable[AccountsJsonName]?.Deserialize<List<Calendar.ConnectedAccount>>(Json) ?? [];
        var merged = ConnectedAccounts.Where(a => a.Source != Calendar.CalendarSource.Ics)
            .Concat(links.Where(a => a.Source == Calendar.CalendarSource.Ics))
            .ToList();
        node[AccountsJsonName] = JsonSerializer.SerializeToNode(merged, Json);

        return Migrate(node.Deserialize<TeezySettings>(Json) ?? this);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TeezySettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new TeezySettings();

            var loaded = JsonSerializer.Deserialize<TeezySettings>(File.ReadAllText(path), Json)
                ?? new TeezySettings();

            return Migrate(loaded);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not stop the app from starting.
            // Defaults are always a usable configuration.
            return new TeezySettings();
        }
    }

    /// <summary>
    /// Converts a settings file written before hotkeys became combinations, and drops calendar
    /// accounts from the retired work-Outlook routes.
    /// </summary>
    internal static TeezySettings Migrate(TeezySettings loaded)
    {
        if (loaded.ConnectedAccounts.Any(a => a.Source == Calendar.CalendarSource.File))
        {
            loaded = loaded with
            {
                ConnectedAccounts = [.. loaded.ConnectedAccounts.Where(a => a.Source != Calendar.CalendarSource.File)],
            };
        }

        if (loaded.LegacyPushToTalkKey is not { Length: > 0 } legacy)
        {
            // A file with neither form - hand-edited, or truncated - still needs a usable key.
            return loaded.Hotkey.IsEmpty ? loaded with { Hotkey = Hotkey.Default } : loaded;
        }

        var migrated = legacy switch
        {
            "RightControl" => new Hotkey(HotkeyKey.RightControl),
            "RightShift" => new Hotkey(HotkeyKey.RightShift),
            "ScrollLock" => new Hotkey(HotkeyKey.ScrollLock),
            "Pause" => new Hotkey(HotkeyKey.Pause),
            "F13" => new Hotkey(HotkeyKey.F13),
            _ => Hotkey.Default,
        };

        // Dropping the legacy field is what makes the migration stick: it is not written back,
        // so the next save leaves a file in the new shape only.
        return loaded with { Hotkey = migrated, LegacyPushToTalkKey = null };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
