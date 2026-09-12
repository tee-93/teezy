using System.Text.Json;

namespace Teezy.Core.Voice;

/// <summary>
/// How many characters have been spoken aloud, by calendar month.
/// </summary>
/// <remarks>
/// <para>
/// Exists because paid speech is sold as a monthly allowance of characters rather than per
/// use, so the only question worth answering — which tier do I need — is unanswerable without
/// knowing what a normal month looks like. Guessing that from the outside is hopeless: it
/// depends entirely on how often you ask questions rather than give commands.
/// </para>
/// <para>
/// Calendar months, not rolling windows, because that is how the allowance resets. A rolling
/// thirty days would be a more stable number and the wrong one.
/// </para>
/// <para>
/// A year is kept. Last month is the figure you actually choose a tier on — this month is
/// always partial, and on the second of the month it is nearly meaningless.
/// </para>
/// </remarks>
public sealed class VoiceUsage
{
    private const int MonthsKept = 12;

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, int> _months;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "voice-usage.json");

    public VoiceUsage(string? path = null)
    {
        _path = path ?? DefaultPath;
        _months = Load(_path);
    }

    /// <summary>Characters spoken in the current calendar month.</summary>
    public int ThisMonth => For(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>Characters spoken in the month before this one.</summary>
    public int LastMonth => For(DateOnly.FromDateTime(DateTime.Now.AddMonths(-1)));

    public int For(DateOnly month)
    {
        lock (_gate) return _months.GetValueOrDefault(Key(month));
    }

    /// <summary>Records a spoken utterance and persists it.</summary>
    /// <remarks>
    /// Counted at the point of asking, not of hearing: a provider bills for what it
    /// synthesised whether or not the user let it finish speaking.
    /// </remarks>
    public void Add(int characters)
    {
        if (characters <= 0) return;

        lock (_gate)
        {
            var key = Key(DateOnly.FromDateTime(DateTime.Now));
            _months[key] = _months.GetValueOrDefault(key) + characters;
            Trim();
            Save();
        }
    }

    private static string Key(DateOnly month) => $"{month.Year:D4}-{month.Month:D2}";

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void Trim()
    {
        if (_months.Count <= MonthsKept) return;

        // Keys sort chronologically because they are zero-padded, which is the whole reason
        // for that format rather than a friendlier one.
        _months = _months
            .OrderByDescending(m => m.Key, StringComparer.Ordinal)
            .Take(MonthsKept)
            .ToDictionary(m => m.Key, m => m.Value);
    }

    private static Dictionary<string, int> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];

            return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                   ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A counter is not worth failing over. Losing the history means a worse tier
            // recommendation, not a broken app.
            return [];
        }
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_months));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // As above: the count is a convenience, not something to interrupt speech for.
        }
    }
}
