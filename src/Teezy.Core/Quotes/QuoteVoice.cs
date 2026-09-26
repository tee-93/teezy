using System.Text.RegularExpressions;

namespace Teezy.Core.Quotes;

/// <summary>Adding a quote by talking, which is how one gets recorded from the car.</summary>
/// <remarks>
/// <para>
/// The moment a quote is worth recording is the moment it is sent, and that is rarely at the
/// desk. So the phrasings here are the ones people actually use out loud — "quoted Hunter
/// Builders four thousand two hundred for door hardware", "sent a quote to Orikan for nine
/// hundred dollars" — and the money is read from the words, since nobody says "dollar sign".
/// </para>
/// <para>
/// Matched here first, before Claude is asked anything: the common case costs nothing, works
/// with no key and no signal, and cannot be talked into doing something else.
/// </para>
/// </remarks>
public static partial class QuoteVoice
{
    [GeneratedRegex(@"^(?:i\s+)?(?:just\s+)?(?:quoted|quote)\s+(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^(?:i\s+)?(?:just\s+)?(?:sent|send|put)\s+(?:a\s+|the\s+)?quote\s+(?:out\s+)?(?:to|for)\s+(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SentAQuote();

    [GeneratedRegex(@"^(?:add|put)\s+(?:a\s+)?quote\s+(?:for|to)\s+(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddAQuote();

    /// <summary>Words that mean money and only get in the way of reading the figure.</summary>
    [GeneratedRegex(@"\b(dollars|dollar|bucks|worth of|for the value of|at a value of|excluding gst|ex gst|plus gst|inc gst|including gst)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneyWords();

    /// <summary>What was said, if it was a quote being recorded; null if it was not.</summary>
    public static ParsedQuote? Add(string? heard, DateOnly today)
    {
        var said = (heard ?? string.Empty).Trim().Trim('.', '!', '?').Trim();
        if (said.Length == 0) return null;

        string? rest = null;
        foreach (var pattern in new[] { Quoted(), SentAQuote(), AddAQuote() })
        {
            if (pattern.Match(said) is { Success: true } match)
            {
                rest = match.Groups["rest"].Value;
                break;
            }
        }

        if (rest is null) return null;

        var parsed = QuoteInput.Parse(MoneyWords().Replace(rest, " "), today);
        return parsed.IsUsable ? parsed : null;
    }

    /// <summary>What TeezyFlow says back when one has been recorded.</summary>
    public static string Spoken(Quote quote, DateOnly today) =>
        QuotePlan.NextChase(quote) is { } next
            ? $"{QuotePlan.Money(quote.Amount)} to {quote.Customer}, {Chase(next, today)}."
            : $"{QuotePlan.Money(quote.Amount)} to {quote.Customer}.";

    private static string Chase(DateOnly next, DateOnly today) => (next.DayNumber - today.DayNumber) switch
    {
        <= 0 => "chasing it today",
        1 => "chasing it tomorrow",
        _ => $"chasing it {next.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture)}",
    };
}
