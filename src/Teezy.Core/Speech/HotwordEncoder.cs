namespace Teezy.Core.Speech;

/// <summary>
/// Turns dictionary hints into the token strings sherpa-onnx will accept as hotwords.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the Parakeet export ships <c>tokens.txt</c> and no <c>bpe.model</c>.
/// Without a BPE model sherpa-onnx cannot split a word itself, so it looks up each
/// whitespace-separated piece of a hotword line directly in the vocabulary — and a plain
/// word is not in there. It says so and skips the word:
/// </para>
/// <code>
/// Cannot find ID for token Phoebe at line: Phoebe
/// Some hotwords failed to encode and were skipped.
/// </code>
/// <para>
/// So the splitting happens here. The vocabulary is a 1025-entry SentencePiece set where
/// <c>▁</c> marks a word start, and greedy longest-match reproduces close enough to the
/// original segmentation to bias the decoder — it does not have to be byte-identical to what
/// the tokenizer would have produced, only to be a valid path through the vocabulary.
/// </para>
/// </remarks>
public static class HotwordEncoder
{
    private const char WordStart = '▁'; // ▁

    /// <summary>Reads the vocabulary, keeping only the token column.</summary>
    public static HashSet<string> LoadVocabulary(string tokensFile)
    {
        var vocab = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(tokensFile))
        {
            // "<token> <id>" — split on the last space, because a token can be a space.
            var cut = line.LastIndexOf(' ');
            if (cut <= 0) continue;
            vocab.Add(line[..cut]);
        }

        return vocab;
    }

    /// <summary>
    /// Encodes each hint, dropping any the vocabulary cannot express.
    /// </summary>
    /// <returns>Newline-separated token lines, or null if nothing could be encoded.</returns>
    public static string? Encode(IEnumerable<string> words, HashSet<string> vocabulary)
    {
        var lines = words
            .Select(w => Segment(w, vocabulary))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>
    /// One word as space-separated tokens, or null if it cannot be segmented.
    /// </summary>
    /// <remarks>
    /// Tried as written first and lowercased second. The vocabulary holds both cases, and a
    /// hint is usually written the way it should be spelled — "Anthropic", not "anthropic" —
    /// so the capitalised form is the one worth biasing toward when it exists.
    /// </remarks>
    public static string? Segment(string word, HashSet<string> vocabulary)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0) return null;

        return SegmentExact(trimmed, vocabulary)
               ?? SegmentExact(trimmed.ToLowerInvariant(), vocabulary);
    }

    private static string? SegmentExact(string word, HashSet<string> vocabulary)
    {
        // Multi-word hints are segmented per word; each word after the first also starts a
        // new SentencePiece unit.
        var pieces = new List<string>();

        foreach (var part in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = WordStart + part;

            while (remaining.Length > 0)
            {
                // Longest match first. A shortest-first walk would consume single characters
                // and produce a token path the decoder never sees in real output.
                var taken = 0;
                for (var length = remaining.Length; length >= 1; length--)
                {
                    if (!vocabulary.Contains(remaining[..length])) continue;
                    taken = length;
                    break;
                }

                if (taken == 0) return null;

                pieces.Add(remaining[..taken]);
                remaining = remaining[taken..];
            }
        }

        return pieces.Count == 0 ? null : string.Join(' ', pieces);
    }
}
