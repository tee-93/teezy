using System.Globalization;

namespace Teezy.Core.Quotes;

/// <summary>One line of a spreadsheet, read as a quote.</summary>
public sealed record ImportedQuote(
    string Customer,
    string What,
    long AmountCents,
    DateOnly Sent,
    string? Reference,
    string? Contact,
    QuoteStatus Status,
    DateOnly? Decided);

/// <summary>What a file turned out to contain, before anything is changed.</summary>
/// <param name="Quotes">The lines that read as quotes.</param>
/// <param name="Skipped">Lines that did not, and why — said by line number, so they can be found.</param>
/// <param name="Columns">Which column each field was taken from, so the guess can be checked.</param>
public sealed record ImportPlan(
    IReadOnlyList<ImportedQuote> Quotes,
    IReadOnlyList<string> Skipped,
    IReadOnlyDictionary<string, string> Columns)
{
    public bool IsEmpty => Quotes.Count == 0;
}

/// <summary>Quotes already in the CRM, brought in from an export.</summary>
/// <remarks>
/// <para>
/// Every CRM names its columns differently, so the headings are matched by what they mean rather
/// than by an exact name, and the plan says which column it took each field from — a guess you
/// can see is a guess you can correct, and a silent one is how a pipeline ends up wrong.
/// </para>
/// <para>
/// <b>Re-running it is safe.</b> Lines are matched on your own quote reference, so importing the
/// same export twice updates rather than duplicates, and a fresh export brings the outcomes with
/// it. Without a reference a line is matched on customer, value and the day it was sent.
/// </para>
/// </remarks>
public static class QuoteImport
{
    private static readonly string[] CustomerNames =
        ["customer", "client", "company", "account", "organisation", "organization", "name", "to"];

    private static readonly string[] WhatNames =
        ["description", "what", "job", "subject", "title", "details", "work", "project", "for"];

    private static readonly string[] AmountNames =
        ["amount", "value", "total", "price", "quoted", "quote value", "ex gst", "excl gst", "net"];

    private static readonly string[] SentNames =
        ["sent", "date", "date sent", "issued", "created", "quote date", "raised"];

    private static readonly string[] ReferenceNames =
        ["reference", "ref", "quote no", "quote number", "number", "quote", "id", "quote id"];

    private static readonly string[] ContactNames = ["contact", "attention", "attn", "person"];

    private static readonly string[] StatusNames = ["status", "outcome", "stage", "result", "state"];

    private static readonly string[] DecidedNames = ["decided", "closed", "won date", "outcome date"];

    /// <summary>Reads a comma-separated export. Nothing is changed by this.</summary>
    public static ImportPlan Read(string csv, DateOnly today)
    {
        var rows = Csv.Parse(csv);
        if (rows.Count < 2)
        {
            return new ImportPlan([], ["The file has no rows under its headings."], new Dictionary<string, string>());
        }

        var headings = rows[0];
        var customer = Column(headings, CustomerNames);
        var amount = Column(headings, AmountNames);

        if (customer < 0 || amount < 0)
        {
            var missing = customer < 0 ? "a customer" : "a value";
            return new ImportPlan([], [$"No column looks like {missing}. The first row must be the headings."],
                new Dictionary<string, string>());
        }

        var what = Column(headings, WhatNames);
        var sent = Column(headings, SentNames);
        var reference = Column(headings, ReferenceNames, except: amount);
        var contact = Column(headings, ContactNames);
        var status = Column(headings, StatusNames);
        var decided = Column(headings, DecidedNames);

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        void Note(string field, int at)
        {
            if (at >= 0 && at < headings.Count) columns[field] = headings[at];
        }

        Note("Customer", customer);
        Note("What", what);
        Note("Value", amount);
        Note("Sent", sent);
        Note("Reference", reference);
        Note("Contact", contact);
        Note("Status", status);

        var quotes = new List<ImportedQuote>();
        var skipped = new List<string>();

        for (var line = 1; line < rows.Count; line++)
        {
            var row = rows[line];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var who = Value(row, customer);
            if (who.Length == 0)
            {
                skipped.Add($"Line {line + 1}: no customer.");
                continue;
            }

            if (Money(Value(row, amount)) is not { } cents)
            {
                skipped.Add($"Line {line + 1}: “{Value(row, amount)}” is not a value.");
                continue;
            }

            quotes.Add(new ImportedQuote(
                who,
                Value(row, what),
                cents,
                Day(Value(row, sent), today) ?? today,
                Text(row, reference),
                Text(row, contact),
                Outcome(Value(row, status)),
                Day(Value(row, decided), today)));
        }

        return new ImportPlan(quotes, skipped, columns);
    }

    /// <summary>
    /// Puts a read file into the store: new quotes added, ones already there brought up to date.
    /// </summary>
    /// <returns>How many were added and how many updated.</returns>
    public static (int Added, int Updated) Apply(QuoteStore store, ImportPlan plan, DateOnly today)
    {
        var existing = store.Visible;
        int added = 0, updated = 0;

        foreach (var row in plan.Quotes)
        {
            var match = Match(existing, row);

            if (match is null)
            {
                var quote = store.Add(row.Customer, row.What, row.AmountCents, row.Sent,
                    reference: row.Reference, contact: row.Contact);

                if (row.Status != QuoteStatus.Open)
                {
                    store.Decide(quote.Id, row.Status, row.Decided ?? today);
                }

                added++;
                continue;
            }

            var edited = match with
            {
                Customer = row.Customer,
                What = row.What.Length > 0 ? row.What : match.What,
                AmountCents = row.AmountCents,
                Sent = row.Sent,
                Reference = row.Reference ?? match.Reference,
                Contact = row.Contact ?? match.Contact,
                Status = row.Status,
                Decided = row.Status == QuoteStatus.Open ? null : row.Decided ?? match.Decided ?? today,
            };

            // Left alone when nothing in the file differs, so a re-import does not stamp every
            // quote as changed and hand every other computer a pointless merge.
            if (Same(edited, match)) continue;

            store.Update(edited);
            updated++;
        }

        return (added, updated);
    }

    private static Quote? Match(IReadOnlyList<Quote> existing, ImportedQuote row) =>
        row.Reference is { Length: > 0 } reference
            ? existing.FirstOrDefault(q =>
                string.Equals(q.Reference, reference, StringComparison.OrdinalIgnoreCase))
            : existing.FirstOrDefault(q =>
                q.AmountCents == row.AmountCents
                && q.Sent == row.Sent
                && string.Equals(q.Customer, row.Customer, StringComparison.OrdinalIgnoreCase));

    private static bool Same(Quote edited, Quote was) =>
        edited.Customer == was.Customer && edited.What == was.What
        && edited.AmountCents == was.AmountCents && edited.Sent == was.Sent
        && edited.Reference == was.Reference && edited.Contact == was.Contact
        && edited.Status == was.Status && edited.Decided == was.Decided;

    /// <summary>The column whose heading means this, or -1.</summary>
    private static int Column(IReadOnlyList<string> headings, IReadOnlyList<string> names, int except = -1)
    {
        // An exact heading beats one that merely contains the word, so "Quote value" is the
        // value and "Quote number" is the reference however they are ordered in the file.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < headings.Count; i++)
            {
                if (i == except) continue;

                var heading = headings[i].Trim().Trim('"').ToLowerInvariant();
                if (heading.Length == 0) continue;

                var hit = pass == 0
                    ? names.Any(n => heading == n)
                    : names.Any(n => heading.Contains(n, StringComparison.Ordinal));

                if (hit) return i;
            }
        }

        return -1;
    }

    private static string Value(IReadOnlyList<string> row, int at) =>
        at >= 0 && at < row.Count ? row[at].Trim() : string.Empty;

    private static string? Text(IReadOnlyList<string> row, int at) =>
        Value(row, at) is { Length: > 0 } value ? value : null;

    /// <summary>"$4,200.00", "4200", "(1,000)" — whatever the CRM felt like writing.</summary>
    internal static long? Money(string text)
    {
        var cleaned = new string([.. text.Where(c => char.IsDigit(c) || c is '.' or '-')]);
        if (cleaned.Length == 0) return null;

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value * 100)
            : null;
    }

    /// <summary>A date in whatever order this computer reads dates, or ISO, which is unambiguous.</summary>
    internal static DateOnly? Day(string text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local)) return local;
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant)) return invariant;

        return Tasks.TaskInput.TryDate(text.Trim(), today, out var spoken) ? spoken : null;
    }

    internal static QuoteStatus Outcome(string text)
    {
        var word = text.Trim().ToLowerInvariant();

        if (word.Length == 0) return QuoteStatus.Open;
        if (word.Contains("won", StringComparison.Ordinal) || word.Contains("accept", StringComparison.Ordinal)
            || word.Contains("order", StringComparison.Ordinal) || word.Contains("success", StringComparison.Ordinal))
        {
            return QuoteStatus.Won;
        }

        string[] lost = ["lost", "decline", "reject", "unsuccessful", "dead", "no"];
        if (lost.Any(l => word == l || word.Contains(l, StringComparison.Ordinal) && l.Length > 2))
        {
            return QuoteStatus.Lost;
        }

        return QuoteStatus.Open;
    }
}

/// <summary>A comma-separated file, read the way the standard says.</summary>
/// <remarks>
/// Written here rather than taken from a package: a CRM export is quoted fields, embedded commas
/// and the occasional newline inside a cell, and that is all this needs to handle.
/// </remarks>
internal static class Csv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }

                // "" inside a quoted field is one quote mark.
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }

                quoted = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
