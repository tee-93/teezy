namespace Teezy.Core.Dictionary;

public enum EntryKind
{
    /// <summary>A word the engine should know exists. Biasing only.</summary>
    Term,

    /// <summary>"When you hear X, write Y." Biasing and the correction pass.</summary>
    Correction,
}

/// <summary>One entry in the personal dictionary.</summary>
public sealed record DictionaryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public EntryKind Kind { get; init; }

    /// <summary>The correct text — the word itself for a Term, the Y for a Correction.</summary>
    public string Write { get; init; } = string.Empty;

    /// <summary>Corrections only: the X in "when you hear X".</summary>
    public string Hear { get; init; } = string.Empty;

    /// <summary>Disabled entries stay in the file but stop firing, so a rule can be
    /// parked without being lost.</summary>
    public bool IsEnabled { get; init; } = true;

    public static DictionaryEntry Term(string word) =>
        new() { Kind = EntryKind.Term, Write = word };

    public static DictionaryEntry Correction(string hear, string write) =>
        new() { Kind = EntryKind.Correction, Hear = hear, Write = write };

    /// <summary>How this entry reads in the plain-text dictionary file.</summary>
    public string ToFileLine()
    {
        var body = Kind == EntryKind.Correction ? $"{Hear} -> {Write}" : Write;
        return IsEnabled ? body : $"# off: {body}";
    }
}
