using System.Text;
using System.Text.RegularExpressions;

namespace Wisper.Core.Dictionary;

/// <summary>A correction that actually fired.</summary>
/// <param name="Heard">What the engine really produced — not the rule's trigger, which can
/// differ in case or spacing.</param>
public sealed record AppliedCorrection(string Heard, string Written, int Count);

/// <summary>Rewrites transcribed text using the dictionary's correction pairs.</summary>
/// <remarks>
/// <para>
/// This is the <i>guaranteed</i> half of the dictionary. Engine biasing only raises the odds
/// of the right word and promises nothing, so anything that must come out right is fixed
/// here, afterwards, deterministically. That is also why the controller runs this pass
/// regardless of whether cleanup is switched on.
/// </para>
/// <para>Three rules, all load-bearing:</para>
/// <list type="number">
/// <item><b>Longest trigger first</b>, so "Claude Code" is applied before "Claude" and the
/// longer rule is not pre-empted by a shorter one overlapping it.</item>
/// <item><b>Whole matches only.</b> Fenced so a rule for "cloud code" can never touch
/// "Cloudflare".</item>
/// <item><b>Glued words still match.</b> Engines run words together, so the gap between
/// parts matches optional whitespace or hyphens: "CloudCode", "cloud-code".</item>
/// </list>
/// </remarks>
public sealed class DictionaryCorrector
{
    /// <summary>Stops a pathological dictionary from wedging a dictation. Matching is linear
    /// here so this should never fire; it exists so a bug cannot hang the app.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly char[] PhraseSeparators = [' ', '-', '\t'];

    private readonly List<Rule> _rules;

    private sealed record Rule(Regex Regex, string Replacement);

    public DictionaryCorrector(IEnumerable<DictionaryEntry> entries)
    {
        // OrderByDescending is a stable sort in LINQ, so equal-length triggers keep file
        // order — the result is reproducible rather than incidental.
        _rules = [.. entries
            .Where(e => e.IsEnabled && e.Kind == EntryKind.Correction)
            .Where(e => !string.IsNullOrWhiteSpace(e.Hear))
            .OrderByDescending(e => e.Hear.Length)
            .Select(e => Compile(e.Hear, e.Write))
            .OfType<Rule>()];
    }

    public bool IsEmpty => _rules.Count == 0;

    public (string Text, IReadOnlyList<AppliedCorrection> Applied) Apply(string text)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(text)) return (text, []);

        // Normalise to NFC before matching. Composed and decomposed forms of the same
        // accented word are different code-point sequences, so without this an accented
        // trigger silently never fires depending on where the text came from.
        var result = text.Normalize(NormalizationForm.FormC);
        var applied = new List<AppliedCorrection>();

        foreach (var rule in _rules)
        {
            var matches = rule.Regex.Matches(result);
            if (matches.Count == 0) continue;

            var heard = matches[0].Value;

            // A MatchEvaluator, not a replacement string: this makes the replacement
            // strictly literal. Plain Replace would treat "$1" and "$&" in the *user's own*
            // replacement text as substitutions, which is a real hazard for arbitrary input.
            result = rule.Regex.Replace(result, _ => rule.Replacement);
            applied.Add(new AppliedCorrection(heard, rule.Replacement, matches.Count));
        }

        return (result, applied);
    }

    /// <summary>Builds the pattern for one trigger phrase.</summary>
    /// <remarks>
    /// The fences are lookarounds on letters and digits rather than <c>\b</c>. <c>\b</c>
    /// treats a trailing hyphen or apostrophe as a boundary and would let a rule bite into a
    /// longer word; requiring that no letter or digit sits on either side is the stricter
    /// guarantee, and it is what keeps "cloud code" off "Cloudflare".
    /// </remarks>
    private static Rule? Compile(string trigger, string replacement)
    {
        var parts = trigger
            .Normalize(NormalizationForm.FormC)
            .Trim()
            .Split(PhraseSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape)
            .ToArray();

        if (parts.Length == 0) return null;

        var body = string.Join(@"[\s\-]*", parts);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){body}(?![\p{{L}}\p{{N}}])";

        try
        {
            // CultureInvariant is not optional: without it, Turkish casing rules make the
            // dotted/dotless I match things it must not.
            return new Rule(
                new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
                replacement);
        }
        catch (ArgumentException)
        {
            return null;   // An un-compilable trigger disables that rule, not the app.
        }
    }

    /// <summary>How many phrases to hand the engine as bias context.</summary>
    /// <remarks>
    /// Deliberately small. Transducer models drift when primed with a long vocabulary — on
    /// quiet or ambiguous audio they begin inventing text from the list, which is a far worse
    /// failure than the misspelling the bias was meant to fix.
    /// </remarks>
    public const int BiasLimit = 40;

    /// <summary>The correct spellings to bias the engine toward, de-duplicated
    /// case-insensitively, in file order, capped at <see cref="BiasLimit"/>.</summary>
    public static IReadOnlyList<string> BiasPhrases(IEnumerable<DictionaryEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();

        foreach (var entry in entries.Where(e => e.IsEnabled))
        {
            var phrase = entry.Write.Trim();
            if (phrase.Length == 0 || !seen.Add(phrase)) continue;
            phrases.Add(phrase);
            if (phrases.Count == BiasLimit) break;
        }
        return phrases;
    }
}
