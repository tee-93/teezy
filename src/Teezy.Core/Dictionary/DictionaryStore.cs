namespace Teezy.Core.Dictionary;

/// <summary>Loads and saves the personal dictionary as plain text.</summary>
/// <remarks>
/// A hand-editable text file rather than JSON, because this is a list a user maintains by
/// hand and a format they can read is worth more than one that round-trips perfectly.
/// <code>
///   Anthropic                 # a Term: just bias the engine toward this spelling
///   cloud code -> Claude Code # a Correction: rewrite the left side to the right
///   # off: teezy -> Teezy   # disabled, kept for later
///   # any other comment
/// </code>
/// </remarks>
public sealed class DictionaryStore
{
    private const string ArrowSeparator = "->";

    private static readonly System.Text.UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

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
        "Teezy", "dictionary.txt");

    public void Reload()
    {
        Entries = File.Exists(Path) ? Parse(File.ReadAllLines(Path)) : [];
        Corrector = new DictionaryCorrector(Entries);
    }

    public void Save(IEnumerable<DictionaryEntry> entries)
    {
        var list = entries.ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // The header is rewritten every time rather than preserved. Saving only entries
        // would silently strip the file's explanation the first time anyone edited the
        // dictionary in the app, leaving a bare list that says nothing about the format
        // to whoever opens it in a text editor later.
        //
        // Written UTF-8 *with* a BOM. Entries legitimately contain accented names, and
        // without the BOM Windows text editors and shells fall back to the ANSI code page
        // and mangle them — which then round-trips the mangled bytes back in on the next
        // load. The BOM is what makes the encoding unambiguous to every tool.
        File.WriteAllLines(Path, [.. Header, .. list.Select(e => e.ToFileLine())], Utf8WithBom);

        Entries = list;
        Corrector = new DictionaryCorrector(list);
    }

    private static IReadOnlyList<string> Header =>
    [
        "# Teezy personal dictionary - edited in the app, or here.",
        "#",
        "#   Anthropic                  a hint: bias the engine toward this spelling",
        "#   cloud code -> Claude Code  a correction: rewrite the left side to the right",
        "#   # off: teezy -> Teezy      disabled, kept for later",
        "#",
        "# Corrections apply longest-trigger-first and match whole words only, so",
        "# 'cloud code' never touches 'Cloudflare'.",
        "",
    ];

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

    /// <summary>Written on first run, so a brand-new file still explains itself.</summary>
    public static IReadOnlyList<string> SampleFile => Header;
}
