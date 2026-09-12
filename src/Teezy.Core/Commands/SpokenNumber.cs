using System.Globalization;

namespace Teezy.Core.Commands;

/// <summary>Turns a spoken quantity into a number.</summary>
/// <remarks>
/// Needed because the recogniser does not consistently write numbers as digits — "set the
/// volume to forty" and "set the volume to 40" are both things Parakeet produces for the same
/// utterance, and a matcher that only understood one of them would look broken at random.
/// <para>
/// Only whole numbers up to a hundred, because the only thing being counted here is a
/// percentage. Anything larger is a misrecognition rather than an intention.
/// </para>
/// </remarks>
public static class SpokenNumber
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40,
        ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>Parses digits or words, or returns null if it is neither.</summary>
    public static int? Parse(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;

        var words = spoken
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0) return null;

        // "a hundred" reads as a quantity; "a" on its own does not.
        if (words is ["a", "hundred"] or ["one", "hundred"] or ["hundred"]) return 100;

        var total = 0;
        var matched = false;

        foreach (var word in words)
        {
            if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
            {
                total += digits;
                matched = true;
                continue;
            }

            if (Tens.TryGetValue(word, out var tens))
            {
                total += tens;
                matched = true;
                continue;
            }

            if (Units.TryGetValue(word, out var unit))
            {
                total += unit;
                matched = true;
                continue;
            }

            // Anything unrecognised means this was not a number at all. Skipping it instead
            // would turn "volume to the max" into a confident zero.
            return null;
        }

        return matched ? total : null;
    }
}
