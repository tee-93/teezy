namespace Teezy.Core.Dictionary;

public enum WarningSeverity
{
    /// <summary>Worth reading, but the entry may well be what the user meant.</summary>
    Caution,

    /// <summary>The entry cannot do anything useful as written.</summary>
    Useless,
}

/// <summary>A reason an entry looks likely to fire on text it was not meant for.</summary>
/// <remarks>
/// <b>Never blocks.</b> It is the user's dictionary, and occasionally rewriting a common word
/// is exactly the point — someone whose company is called "Slack" wants every "slack"
/// capitalised. These exist because a correction rewrites text silently and after the fact,
/// so a bad rule is very hard to notice: the transcript is simply wrong in a plausible way.
/// </remarks>
public sealed record DictionaryWarning(WarningSeverity Severity, string Message)
{
    /// <summary>
    /// Ordinary English words that would fire constantly as a whole trigger.
    /// </summary>
    /// <remarks>
    /// Deliberately short. This catches the obvious foot-guns rather than trying to be a
    /// dictionary of its own — a long list would start warning about legitimate jargon.
    /// </remarks>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "all", "and", "any", "are", "as", "at", "back", "be", "but", "by",
        "call", "can", "case", "check", "class", "close", "cloud", "code", "come", "could",
        "data", "day", "do", "does", "down", "each", "even", "file", "find", "first", "for",
        "from", "get", "give", "go", "good", "great", "group", "had", "has", "have", "he",
        "her", "here", "him", "his", "how", "if", "in", "into", "is", "it", "its", "just",
        "key", "know", "like", "line", "list", "look", "make", "man", "many", "may", "me",
        "more", "most", "my", "need", "new", "no", "not", "now", "number", "of", "off", "on",
        "one", "only", "open", "or", "other", "our", "out", "over", "page", "part", "people",
        "point", "put", "read", "right", "run", "said", "same", "say", "see", "set", "she",
        "should", "show", "side", "so", "some", "state", "still", "such", "take", "team",
        "test", "than", "that", "the", "their", "them", "then", "there", "these", "they",
        "thing", "think", "this", "time", "to", "two", "type", "up", "us", "use", "user",
        "very", "want", "was", "way", "we", "well", "were", "what", "when", "where", "which",
        "who", "will", "with", "word", "work", "would", "year", "you", "your",
    };

    private static readonly char[] PhraseSeparators = [' ', '-', '\t'];

    /// <summary>Everything questionable about one entry.</summary>
    public static IReadOnlyList<DictionaryWarning> Check(DictionaryEntry entry)
    {
        // Only the trigger side can misfire. A Term is never matched against text — it only
        // nudges the engine — so there is nothing here it can get wrong.
        if (entry.Kind != EntryKind.Correction) return [];

        var trigger = entry.Hear.Trim();
        if (trigger.Length == 0) return [];

        var warnings = new List<DictionaryWarning>();
        var words = trigger.Split(PhraseSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 1)
        {
            var only = words[0];
            if (Common.Contains(only))
            {
                warnings.Add(new DictionaryWarning(WarningSeverity.Caution,
                    $"“{trigger}” is an ordinary word. This rewrites every use of it, not just "
                    + "the ones you mean. A longer phrase is usually safer."));
            }
            else if (only.Length <= 3)
            {
                warnings.Add(new DictionaryWarning(WarningSeverity.Caution,
                    $"“{trigger}” is very short and will match often. Consider a longer phrase."));
            }
        }

        // Ordinal, NOT OrdinalIgnoreCase. Fixing capitalisation is one of the most common
        // reasons to add an entry at all — "kubernetes" to "Kubernetes" changes the output
        // even though the two are equal ignoring case, and warning about it would be wrong.
        if (string.Equals(entry.Write.Trim(), trigger, StringComparison.Ordinal))
        {
            warnings.Add(new DictionaryWarning(WarningSeverity.Useless,
                $"This rewrites “{trigger}” to itself, so it will never change anything."));
        }

        return warnings;
    }
}
