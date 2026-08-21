using System.Text.Json.Serialization;
namespace Teezy.Core.History;

/// <summary>One finished dictation, as it was typed.</summary>
/// <remarks>
/// Stored so the user can find something they dictated earlier and copy it again — losing a
/// paragraph because the target window ate it is the most annoying failure this app can have,
/// and history is the cheap insurance against it.
/// </remarks>
public sealed record HistoryEntry
{
    public required string Id { get; init; }

    /// <summary>When the key was released. Stored with offset so a timeline stays correct
    /// across daylight saving and time-zone changes.</summary>
    public required DateTimeOffset At { get; init; }

    public required string Text { get; init; }

    /// <summary>How long the key was held.</summary>
    public double AudioSeconds { get; init; }

    /// <summary>Release to injected — the wait the user actually experienced.</summary>
    public double ProcessingMs { get; init; }

    /// <summary>Process name of the window the text went into, for the usage breakdown.</summary>
    public string? App { get; init; }

    public int Corrections { get; init; }

    /// <remarks>
    /// Not serialised. Derived values in the file would bloat every line and, worse, could be
    /// read back as authoritative after the text was edited or the counting rule changed.
    /// </remarks>
    [JsonIgnore]
    public int WordCount => CountWords(Text);

    /// <summary>Speaking rate. The headline number, so it is defined in one place.</summary>
    /// <remarks>
    /// Zero for implausibly short holds rather than a huge number: three words in 0.2 s is
    /// 900 wpm and would wreck any average it landed in.
    /// </remarks>
    [JsonIgnore]
    public double WordsPerMinute =>
        AudioSeconds < 0.5 ? 0 : WordCount / AudioSeconds * 60.0;

    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
