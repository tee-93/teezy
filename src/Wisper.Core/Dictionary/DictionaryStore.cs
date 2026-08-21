namespace Wisper.Core.Dictionary;

/// <summary>Loads and saves the personal dictionary as plain text.</summary>
/// <remarks>
/// A hand-editable text file rather than JSON, because this is a list a user maintains by
/// hand and a format they can read is worth more than one that round-trips perfectly.
/// <code>
///   Anthropic                 # a Term: just bias the engine toward this spelling
///   cloud code -> Claude Code # a Correction: rewrite the left side to the right
///   # off: wisper -> Wisper   # disabled, kept for later
///   # any other comment
/// </code>
/// </remarks>
public sealed class DictionaryStore
{
    private const string ArrowSeparator = "->";

    public string Path { get; }
    public IReadOnlyList<DictionaryEntry> Entries { get; private set; } = [];
    public DictionaryCorrector Corrector { get; private set; } = new([]);

    public DictionaryStore(string path)
    {
        Path = path;
        Reload();
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wisper", "dictionary.txt");

    public void Reload()
    {
        Entries = File.Exists(Path) ? Parse(File.ReadAllLines(Path)) : [];
        Corrector = new DictionaryCorrector(Entries);
    }

    public void Save(IEnumerable<DictionaryEntry> entries)
    {
        var list = entries.ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllLines(Path, list.Select(e => e.ToFileLine()));
        Entries = list;
        Corrector = new DictionaryCorrector(list);
    }

    internal static List<DictionaryEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<DictionaryEntry>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // "# off:" is a disabled entry, not a comment. Everything else starting with #
            // really is a comment and is dropped.
            var enabled = true;
            if (line.StartsWith('#'))
            {
                var body = line[1..].TrimStart();
                if (!body.StartsWith("off:", StringComparison.OrdinalIgnoreCase)) continue;
                line = body[4..].Trim();
                enabled = false;
                if (line.Length == 0) continue;
            }

            var arrow = line.IndexOf(ArrowSeparator, StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var hear = line[..arrow].Trim();
                var write = line[(arrow + ArrowSeparator.Length)..].Trim();
                if (hear.Length == 0 || write.Length == 0) continue;
                entries.Add(DictionaryEntry.Correction(hear, write) with { IsEnabled = enabled });
            }
            else
            {
                entries.Add(DictionaryEntry.Term(line) with { IsEnabled = enabled });
            }
        }

        return entries;
    }

    /// <summary>A starter dictionary, written on first run so the file explains itself.</summary>
    public static IReadOnlyList<string> SampleFile =>
    [
        "# Wisper personal dictionary.",
        "#",
        "#   Anthropic                  a word to bias the engine toward",
        "#   cloud code -> Claude Code  rewrite the left side to the right",
        "#   # off: rule -> Rule        disabled, kept for later",
        "#",
        "# Corrections are applied longest-trigger-first and match whole words only,",
        "# so 'cloud code' will never touch 'Cloudflare'.",
        "",
    ];
}
