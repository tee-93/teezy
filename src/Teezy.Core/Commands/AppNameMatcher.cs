namespace Teezy.Core.Commands;

/// <summary>
/// Picks which installed application someone meant.
/// </summary>
/// <remarks>
/// <para>
/// The scoring, not the searching. What is installed is the platform's business; choosing
/// between "Chrome", "Google Chrome" and "Google Chrome Canary" given the word "chrome" is
/// ordinary logic with an easy wrong answer, so it lives here where it can be tested.
/// </para>
/// <para>
/// <b>Shorter wins ties.</b> Someone saying a short name almost always means the plain thing
/// rather than the variant — "chrome" is Chrome, not Chrome Canary — and preferring the
/// shortest match is what encodes that.
/// </para>
/// </remarks>
public static class AppNameMatcher
{
    /// <summary>The best candidate for what was said, or null if nothing is close enough.</summary>
    public static string? Best(string? spoken, IEnumerable<string> candidates)
    {
        var wanted = (spoken ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;

        string? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = Score(wanted, candidate);
            if (score == 0) continue;

            // Strictly better, or equally good and shorter.
            if (score > bestScore || (score == bestScore && best is not null && candidate.Length < best.Length))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>How well a candidate answers what was said. Zero means not at all.</summary>
    /// <remarks>
    /// Deliberately crude, and deliberately not fuzzy. Edit distance would let "teams" reach
    /// "Teamviewer", and launching the wrong application is a worse failure than admitting
    /// nothing matched — the user can always say it differently.
    /// </remarks>
    private static int Score(string wanted, string candidate)
    {
        if (candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return 4;

        // "chrome" finding "Google Chrome": a whole word, not a fragment.
        var words = candidate.Split(
            [' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Any(w => w.Equals(wanted, StringComparison.OrdinalIgnoreCase))) return 3;

        if (candidate.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return 2;

        // Last resort, and only for something long enough to be meant. Two letters inside a
        // name is a coincidence, not a request.
        if (wanted.Length >= 4 && candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase)) return 1;

        return 0;
    }
}
