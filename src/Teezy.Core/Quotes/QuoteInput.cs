using System.Globalization;
using System.Text.RegularExpressions;
using Teezy.Core.Tasks;

namespace Teezy.Core.Quotes;

/// <summary>What a typed or spoken quote line said.</summary>
/// <param name="Customer">Who it is for; empty when the line could not be read.</param>
/// <param name="What">What it is for; empty when the line said nothing but a customer and a figure.</param>
/// <param name="AmountCents">The value in cents; zero when no figure was found.</param>
/// <param name="Sent">The day it went out, when the line said; otherwise null, meaning today.</param>
/// <param name="Reference">A quote number, if one was written as <c>#1234</c> or "ref 1234".</param>
public sealed record ParsedQuote(
    string Customer, string What, long AmountCents, DateOnly? Sent, string? Reference)
{
    public bool IsUsable => Customer.Length > 0 && AmountCents > 0;
}

/// <summary>
/// Reads a quote written the way it is said: "Hunter Builders $4,200 door hardware sent Friday".
/// </summary>
/// <remarks>
/// <para>
/// One line, four things, and only one of them is unmistakable: the money. So the figure is
/// found first and everything is placed around it — what comes before is the customer, what
/// comes after is the job — which is the order people say it in and, conveniently, the order
/// they type it in too.
/// </para>
/// <para>
/// Dates are read by <see cref="TaskInput.TryDate"/>, so "friday", "yesterday" and "25/9" mean
/// the same here as in quick add. Spoken money ("four thousand two hundred") is handled as well
/// as typed, because the same reader takes what the assistant heard.
/// </para>
/// </remarks>
public static partial class QuoteInput
{
    [GeneratedRegex(@"(?:^|\s)(?:#|ref\.?\s*|quote\s*(?:no\.?|number)?\s*)(?<ref>[\p{L}]{0,4}[\p{N}][\p{L}\p{N}\-/]*)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceTag();

    /// <summary>A figure: "$4,200", "4200", "4.2k", "$4,207.35".</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\$?(?<n>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)(?<k>\s?k\b)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Figure();

    /// <summary>Words that introduce the job rather than belong to it.</summary>
    private static readonly string[] Leads = ["for", "to supply", "to", "on", "covering", "of"];

    /// <summary>Words that introduce the customer and are not part of their name.</summary>
    private static readonly string[] Openers = ["quoted", "quote for", "quote to", "quote", "sent", "for"];

    public static ParsedQuote Parse(string text, DateOnly today)
    {
        var line = (text ?? string.Empty).Trim();
        if (line.Length == 0) return new ParsedQuote(string.Empty, string.Empty, 0, null, null);

        string? reference = null;
        if (ReferenceTag().Match(line) is { Success: true } tag)
        {
            reference = tag.Groups["ref"].Value;
            line = line.Remove(tag.Index, tag.Length).Trim();
        }

        var (rest, sent) = TakeDate(line, today);

        var (before, amount, after) = TakeAmount(rest);
        if (amount == 0) (before, amount, after) = TakeSpokenAmount(rest);

        var customer = StripTail(Tidy(StripOpeners(before)));
        var what = Tidy(StripLead(after));

        // "4,200 for Hunter Builders, door hardware" — said the other way round, the customer is
        // after the figure. Only when nothing at all came before it, so it cannot mislead.
        if (customer.Length == 0 && what.Length > 0)
        {
            var parts = what.Split(',', 2);
            customer = Tidy(parts[0]);
            what = parts.Length > 1 ? Tidy(parts[1]) : string.Empty;
        }

        return new ParsedQuote(customer, what, amount, sent, reference);
    }

    /// <summary>The money in a line, and what sits either side of it.</summary>
    private static (string Before, long Cents, string After) TakeAmount(string line)
    {
        foreach (Match match in Figure().Matches(line))
        {
            var digits = match.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;

            // "4.2k" is four thousand two hundred; a bare "4.2" is four dollars twenty.
            if (match.Groups["k"].Success) value *= 1000;

            // A year or a small number on its own is not a price. A dollar sign settles it.
            var marked = match.Value.TrimStart().StartsWith('$') || match.Groups["k"].Success;
            if (!marked && value < 100) continue;

            return (line[..match.Index], (long)Math.Round(value * 100), line[(match.Index + match.Length)..]);
        }

        return (line, 0, string.Empty);
    }

    /// <summary>Money said in words, as the assistant hears it: "four thousand two hundred".</summary>
    private static (string Before, long Cents, string After) TakeSpokenAmount(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var start = 0; start < words.Length; start++)
        {
            if (Number(words[start]) is null && !IsScale(words[start])) continue;

            var end = start;
            while (end + 1 < words.Length
                   && (Number(words[end + 1]) is not null || IsScale(words[end + 1])
                       || string.Equals(words[end + 1], "and", StringComparison.OrdinalIgnoreCase)))
            {
                end++;
            }

            if (SpokenValue(words[start..(end + 1)]) is not { } value || value < 100) continue;

            return (string.Join(' ', words[..start]), value * 100, string.Join(' ', words[(end + 1)..]));
        }

        return (line, 0, string.Empty);
    }

    /// <summary>"four thousand two hundred" → 4200; null when the words do not add up.</summary>
    internal static long? SpokenValue(IReadOnlyList<string> words)
    {
        long total = 0, current = 0;
        var any = false;

        foreach (var word in words)
        {
            if (string.Equals(word, "and", StringComparison.OrdinalIgnoreCase)) continue;

            if (Number(word) is { } number)
            {
                current += number;
                any = true;
                continue;
            }

            var scale = Scale(word);
            if (scale is null) return null;

            any = true;
            if (scale == 100)
            {
                current = Math.Max(current, 1) * 100;
            }
            else
            {
                total += Math.Max(current, 1) * scale.Value;
                current = 0;
            }
        }

        return any ? total + current : null;
    }

    private static bool IsScale(string word) => Scale(word) is not null;

    private static long? Scale(string word) => word.ToLowerInvariant().TrimEnd('s') switch
    {
        "hundred" => 100,
        "thousand" or "k" => 1000,
        "million" => 1_000_000,
        _ => null,
    };

    private static long? Number(string word) => word.ToLowerInvariant().Trim(',', '.') switch
    {
        "zero" => 0, "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
        "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
        "eleven" => 11, "twelve" => 12, "thirteen" => 13, "fourteen" => 14, "fifteen" => 15,
        "sixteen" => 16, "seventeen" => 17, "eighteen" => 18, "nineteen" => 19,
        "twenty" => 20, "thirty" => 30, "forty" => 40, "fifty" => 50,
        "sixty" => 60, "seventy" => 70, "eighty" => 80, "ninety" => 90,
        var w when long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    /// <summary>A day at the end of the line — "sent Friday", "yesterday" — and the rest without it.</summary>
    private static (string Line, DateOnly? Sent) TakeDate(string line, DateOnly today)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        for (var take = Math.Min(3, words.Count - 1); take >= 1; take--)
        {
            var tail = string.Join(' ', words.TakeLast(take));
            if (!TaskInput.TryDate(tail, today, out var date)) continue;

            // A quote has already been sent, so its day is behind us. Quick add reads "Friday"
            // and "25 Aug" as the next one, because a task is something still to do; here the
            // same words mean the Friday just gone and the August just gone.
            if (date > today)
            {
                if (IsWeekday(tail)) date = date.AddDays(-7);
                else if (!HasYear(tail)) date = date.AddYears(-1);
                else continue;   // A written-out future date is not the day it was sent.
            }

            words.RemoveRange(words.Count - take, take);

            // "sent" only led up to the date; it is not part of the job.
            if (words.Count > 0 && string.Equals(words[^1], "sent", StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(words.Count - 1);
            }

            return (string.Join(' ', words), date);
        }

        return (line, null);
    }

    /// <summary>
    /// The word that led into the figure is not part of the customer's name: "a quote to Orikan
    /// for nine hundred" leaves "Orikan for" behind, and no company is called that.
    /// </summary>
    private static string StripTail(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (words.Count > 1 && Tails.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }

        return words.Count == 1 && Tails.Contains(words[0], StringComparer.OrdinalIgnoreCase)
            ? string.Empty
            : string.Join(' ', words);
    }

    private static readonly string[] Tails = ["for", "of", "at", "to", "on", "about", "is", "was", "worth"];

    /// <summary>Whether the words name a year, which settles the matter either way.</summary>
    private static bool HasYear(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<!d)d{4}(?!d)");

    private static bool IsWeekday(string text)
    {
        var word = text.Trim().ToLowerInvariant();
        return Days.Any(d => word == d || word == d[..3]);
    }

    private static readonly string[] Days =
        ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    private static string StripOpeners(string text)
    {
        var line = text.Trim();

        foreach (var opener in Openers)
        {
            if (line.StartsWith(opener + " ", StringComparison.OrdinalIgnoreCase))
            {
                return StripOpeners(line[(opener.Length + 1)..]);
            }
        }

        return line;
    }

    private static string StripLead(string text)
    {
        var line = text.Trim().TrimStart(',', '-', '–');

        foreach (var lead in Leads)
        {
            if (line.StartsWith(lead + " ", StringComparison.OrdinalIgnoreCase))
            {
                return line[(lead.Length + 1)..].Trim();
            }
        }

        return line;
    }

    private static string Tidy(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',', '-', '–', '.');
}
