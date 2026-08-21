using System.Text;
using System.Text.Json;

namespace Teezy.Core.History;

/// <summary>Append-only log of everything dictated.</summary>
/// <remarks>
/// <para>
/// JSON Lines, not a single JSON array and not a database. Appending one line per utterance
/// is O(1) and never rewrites what is already on disk, so a crash mid-write can damage at
/// most the last entry — whereas re-serialising a growing array would put the entire history
/// at risk on every dictation.
/// </para>
/// <para>
/// A torn final line is therefore expected rather than exceptional, and <see cref="Load"/>
/// skips lines it cannot parse instead of failing.
/// </para>
/// </remarks>
public sealed class HistoryStore
{
    /// <summary>Entries kept. Old ones are dropped when the file is compacted.</summary>
    public const int MaxEntries = 5_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _gate = new();

    public string Path { get; }

    public HistoryStore(string? path = null) => Path = path ?? DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "history.jsonl");

    /// <summary>Raised after a successful append, so open windows can refresh.</summary>
    public event Action<HistoryEntry>? Added;

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, JsonSerializer.Serialize(entry, Json) + Environment.NewLine, Encoding.UTF8);
        }
        Added?.Invoke(entry);
    }

    /// <summary>Every entry, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return [];

            var entries = new List<HistoryEntry>();
            foreach (var line in File.ReadLines(Path))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } entry)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A torn last line from an interrupted write. Skipping one utterance is
                    // strictly better than refusing to show any history at all.
                }
            }

            entries.Reverse();
            return entries;
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return;
            var kept = File.ReadLines(Path)
                .Where(l => l.Length > 0 && !l.Contains($"\"Id\":\"{id}\"", StringComparison.Ordinal))
                .ToList();
            File.WriteAllLines(Path, kept, Encoding.UTF8);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    /// <summary>Trims the file to <see cref="MaxEntries"/>, oldest dropped first.</summary>
    public void Compact()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return;
            var lines = File.ReadAllLines(Path).Where(l => l.Length > 0).ToList();
            if (lines.Count <= MaxEntries) return;
            File.WriteAllLines(Path, lines.Skip(lines.Count - MaxEntries), Encoding.UTF8);
        }
    }
}
