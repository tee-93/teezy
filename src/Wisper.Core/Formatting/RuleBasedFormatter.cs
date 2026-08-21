using System.Text;
using System.Text.RegularExpressions;

namespace Wisper.Core.Formatting;

/// <summary>Deterministic, zero-latency cleanup. Offline, free, and never wrong in a
/// surprising way — which is why it stays the fallback even once an LLM tier exists.</summary>
/// <remarks>
/// <para>
/// <b>Tuned for what Parakeet TDT v2 actually emits, which is already punctuated and
/// sentence-cased.</b> That is unusual for an ASR model and it changes the job: this pass is
/// not adding punctuation from scratch, it is removing disfluency and honouring spoken
/// commands. Every rule here is written to be idempotent so it cannot fight the model —
/// re-capitalising an already-capital letter is a no-op, and terminal punctuation is only
/// added when genuinely absent.
/// </para>
/// <para>
/// Ordering is load-bearing: fillers go first (before spacing is normalised, so the double
/// space they leave behind gets collapsed), spoken commands next (while sentence structure
/// is still intact), then whitespace, then casing, then the final full stop.
/// </para>
/// </remarks>
public sealed partial class RuleBasedFormatter : ITextFormatter
{
    /// <summary>
    /// Hesitation sounds, removed with any comma the model attached to them.
    /// </summary>
    /// <remarks>
    /// <b>"like" and "you know" are deliberately not here.</b> They are real words far more
    /// often than they are filler ("a list like this one"), and a cleanup pass that eats
    /// meaning is worse than one that leaves a stray "um".
    /// </remarks>
    private static readonly string[] Fillers = ["um", "uh", "erm", "uhm", "hmm", "mhm", "ah"];

    /// <summary>Punctuation people genuinely speak aloud mid-dictation.</summary>
    private static readonly (string Spoken, string Written)[] SpokenPunctuation =
    [
        ("new paragraph", "\n\n"),
        ("new line",      "\n"),
        ("open paren",    " ("),
        ("close paren",   ") "),
        ("open quote",    " \""),
        ("close quote",   "\" "),
    ];

    [GeneratedRegex(@"[ \t]{2,}")]                     private static partial Regex MultiSpace();
    [GeneratedRegex(@" +([,.!?;:])")]                  private static partial Regex SpaceBeforePunct();
    [GeneratedRegex(@"\n{3,}")]                        private static partial Regex ExcessBlankLines();
    [GeneratedRegex(@"[ \t]*\n[ \t]*")]                private static partial Regex PaddedNewline();

    /// <summary>
    /// "scratch that" / "delete that" — drop everything said before it in that sentence.
    /// </summary>
    /// <remarks>
    /// Scoped to the current sentence rather than the whole utterance on purpose. Wiping
    /// several correct sentences because of one trailing command is a much worse failure
    /// than leaving a little debris behind.
    /// </remarks>
    [GeneratedRegex(@"(?i)(?:^|(?<=[.!?]\s))[^.!?]*?\b(?:scratch|delete|ignore) that\b[,.]?\s*")]
    private static partial Regex ScratchThat();

    public Task<string> FormatAsync(string raw, CancellationToken ct = default)
    {
        var text = raw.Trim();
        if (text.Length == 0) return Task.FromResult(text);

        text = StripFillers(text);
        text = ScratchThat().Replace(text, string.Empty);
        text = ApplySpokenPunctuation(text);
        text = CollapseWhitespace(text);
        text = CapitalizeSentences(text);
        text = EnsureTerminalPunctuation(text);

        return Task.FromResult(text);
    }

    private static string StripFillers(string text)
    {
        foreach (var filler in Fillers)
        {
            // Fenced by a lookbehind on word-or-apostrophe rather than \b, so "uh" cannot
            // bite the "uh" out of "uh-huh" or a name. The optional trailing comma catches
            // the model's habit of writing "Um, so..." as a clause.
            text = Regex.Replace(
                text,
                $@"(?i)(?<![\w'])[{filler[0]}{char.ToUpperInvariant(filler[0])}]{Regex.Escape(filler[1..])}\b,?",
                string.Empty);
        }
        return text;
    }

    private static string ApplySpokenPunctuation(string text)
    {
        foreach (var (spoken, written) in SpokenPunctuation)
        {
            text = Regex.Replace(text, $@"(?i)\b{Regex.Escape(spoken)}\b", written);
        }
        return text;
    }

    private static string CollapseWhitespace(string text)
    {
        text = MultiSpace().Replace(text, " ");
        text = SpaceBeforePunct().Replace(text, "$1");
        text = PaddedNewline().Replace(text, "\n");
        text = ExcessBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>Upper-cases the first letter of each sentence. Idempotent by construction.</summary>
    private static string CapitalizeSentences(string text)
    {
        var sb = new StringBuilder(text.Length);
        var capitalizeNext = true;

        foreach (var c in text)
        {
            if (capitalizeNext && char.IsLetter(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n') capitalizeNext = true;
            }
        }
        return sb.ToString();
    }

    private static string EnsureTerminalPunctuation(string text)
    {
        if (text.Length == 0) return text;
        var last = text[^1];
        return char.IsLetterOrDigit(last) ? text + "." : text;
    }
}
