using Teezy.Core.Tasks;

namespace Teezy.Core.Quotes;

/// <summary>Where a quote ended up.</summary>
public enum QuoteStatus
{
    Open,
    Won,
    Lost,
}

/// <summary>Which pile a quote belongs in on the page.</summary>
public enum QuoteBucket
{
    /// <summary>Its next chase is today or overdue.</summary>
    ToChase,

    /// <summary>Open, and nothing has happened on it for weeks.</summary>
    Quiet,

    /// <summary>Open, chased recently enough, nothing to do today.</summary>
    Open,

    /// <summary>Won or lost.</summary>
    Decided,
}

/// <summary>One quote sent to a customer.</summary>
/// <param name="Id">Stable across computers, so sync can match the same quote on each.</param>
/// <param name="Customer">Who it went to — the company, as you would say it out loud.</param>
/// <param name="What">What it is for, in a few words.</param>
/// <param name="AmountCents">
/// The value, in whole cents. Money is never a <c>double</c> here: a quote is a number someone
/// will be held to, and 4,207.35 typed into a binary fraction stops being that number.
/// </param>
/// <param name="Sent">The day it went out. Chasing is counted from here.</param>
/// <param name="Cadence">
/// Days after sending to chase on, copied from the settings when the quote is made so that
/// changing the setting later does not silently re-time quotes already out.
/// </param>
/// <param name="Chased">How many chases have been done.</param>
/// <param name="LastChased">The day of the last one; null until the first.</param>
/// <param name="Status">Open, won or lost.</param>
/// <param name="Decided">The day it was won or lost.</param>
/// <param name="Reference">Your own quote number, if it has one. What a CRM import matches on.</param>
/// <param name="Contact">The person at that customer.</param>
/// <param name="Notes">Running notes, oldest first — the same shape as a task's.</param>
/// <param name="Emails">Emails dropped onto the quote, kept apart from the notes.</param>
/// <param name="ChaseTaskId">The task doing the chasing now, so the two stay in step.</param>
/// <param name="Modified">When it last changed, anywhere. The newer copy wins when computers disagree.</param>
/// <param name="Deleted">Deleted, kept as a marker so the deletion reaches the other computers.</param>
public sealed record Quote(
    string Id,
    string Customer,
    string What,
    long AmountCents,
    DateOnly Sent,
    IReadOnlyList<int> Cadence,
    int Chased,
    DateOnly? LastChased,
    QuoteStatus Status,
    DateOnly? Decided,
    string? Reference,
    string? Contact,
    IReadOnlyList<TaskNote> Notes,
    DateTimeOffset Modified,
    IReadOnlyList<TaskEmail>? Emails = null,
    string? ChaseTaskId = null,
    bool Deleted = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpen => Status == QuoteStatus.Open && !Deleted;

    /// <summary>The attached emails, never null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TaskEmail> AllEmails => Emails ?? [];

    /// <summary>The value in dollars, for display and for adding up.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Amount => AmountCents / 100m;

    /// <summary>The last thing that happened: a chase, or the day it went out.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly LastMoved => LastChased is { } chased && chased > Sent ? chased : Sent;

    public static Quote New(
        string customer,
        string what,
        long amountCents,
        DateOnly sent,
        DateTimeOffset now,
        IReadOnlyList<int>? cadence = null,
        string? reference = null,
        string? contact = null) =>
        new(Guid.NewGuid().ToString("N"),
            customer.Trim(),
            what.Trim(),
            amountCents,
            sent,
            cadence is { Count: > 0 } ? [.. cadence] : QuotePlan.DefaultCadence,
            Chased: 0,
            LastChased: null,
            QuoteStatus.Open,
            Decided: null,
            Clean(reference),
            Clean(contact),
            Notes: [],
            Modified: now);

    internal static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>How much is out, what is due a chase, and what was won.</summary>
/// <param name="Open">How many quotes are still open, and what they are worth.</param>
/// <param name="Won">Won this month, and for how much.</param>
/// <param name="Lost">Lost this month.</param>
/// <param name="WinRate">Won over decided this month, 0 to 1; null when nothing was decided.</param>
public sealed record QuoteTotals(
    (int Count, decimal Value) Open,
    (int Count, decimal Value) Won,
    (int Count, decimal Value) Lost,
    double? WinRate);

/// <summary>When to chase, what has gone quiet, and how the month is going.</summary>
/// <remarks>
/// <para>
/// A quote is not a task, but every chase is one, so the chasing itself lives in the task list —
/// where the reminders, the focus card, Home and the morning briefing already work. What is
/// here is only the arithmetic: which day the next chase falls on, when something has gone
/// quiet, and what the month adds up to.
/// </para>
/// </remarks>
public static class QuotePlan
{
    /// <summary>
    /// Three days, a week, a fortnight — then monthly. Fast enough to be useful while the job is
    /// still live, slow enough not to become the thing the customer remembers you for.
    /// </summary>
    public static IReadOnlyList<int> DefaultCadence => [3, 7, 14];

    /// <summary>Monthly, once the cadence has been worked through.</summary>
    public const int ThereafterDays = 30;

    /// <summary>Nothing said or done for three weeks: the quote has gone quiet.</summary>
    public const int QuietDays = 21;

    /// <summary>The day the next chase falls on, or null once a quote is decided.</summary>
    public static DateOnly? NextChase(Quote quote)
    {
        if (!quote.IsOpen) return null;

        var from = quote.Chased < quote.Cadence.Count
            ? quote.Sent.AddDays(quote.Cadence[quote.Chased])
            : quote.LastMoved.AddDays(ThereafterDays);

        // Nobody reads a chasing email on a Sunday, and a follow-up that lands then is answered
        // on Monday anyway — so it is booked for the Monday.
        return TaskPlan.Workday(from);
    }

    /// <summary>Open, and nothing has happened on it for <see cref="QuietDays"/>.</summary>
    public static bool IsQuiet(Quote quote, DateOnly today) =>
        quote.IsOpen && today.DayNumber - quote.LastMoved.DayNumber >= QuietDays;

    /// <summary>Where a quote belongs on the page today.</summary>
    public static QuoteBucket BucketOf(Quote quote, DateOnly today)
    {
        if (!quote.IsOpen) return QuoteBucket.Decided;
        if (NextChase(quote) is { } due && due <= today) return QuoteBucket.ToChase;
        return IsQuiet(quote, today) ? QuoteBucket.Quiet : QuoteBucket.Open;
    }

    /// <summary>
    /// The quotes in each pile: chases due first, oldest first, because the one that has been
    /// waiting longest is the one at risk.
    /// </summary>
    public static IReadOnlyList<(QuoteBucket Bucket, IReadOnlyList<Quote> Quotes)> Arrange(
        IEnumerable<Quote> quotes, DateOnly today) =>
    [
        .. quotes
            .Where(q => !q.Deleted)
            .GroupBy(q => BucketOf(q, today))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<Quote>)
            [
                .. g.Key == QuoteBucket.Decided
                    ? g.OrderByDescending(q => q.Decided ?? q.Sent)
                    : g.OrderBy(q => NextChase(q) ?? q.Sent).ThenBy(q => q.Sent),
            ])),
    ];

    /// <summary>Everything due a chase today or earlier, oldest first.</summary>
    public static IReadOnlyList<Quote> DueToChase(IEnumerable<Quote> quotes, DateOnly today) =>
        [.. quotes.Where(q => BucketOf(q, today) == QuoteBucket.ToChase)
            .OrderBy(q => NextChase(q) ?? q.Sent)];

    /// <summary>What is open, and how the month that contains <paramref name="today"/> went.</summary>
    public static QuoteTotals Totals(IEnumerable<Quote> quotes, DateOnly today)
    {
        var all = quotes.Where(q => !q.Deleted).ToList();
        var open = all.Where(q => q.IsOpen).ToList();

        bool ThisMonth(Quote q) =>
            q.Decided is { } decided && decided.Year == today.Year && decided.Month == today.Month;

        var won = all.Where(q => q.Status == QuoteStatus.Won && ThisMonth(q)).ToList();
        var lost = all.Where(q => q.Status == QuoteStatus.Lost && ThisMonth(q)).ToList();
        var decided = won.Count + lost.Count;

        return new QuoteTotals(
            (open.Count, open.Sum(q => q.Amount)),
            (won.Count, won.Sum(q => q.Amount)),
            (lost.Count, lost.Sum(q => q.Amount)),
            decided == 0 ? null : (double)won.Count / decided);
    }

    /// <summary>What the chase task for a quote is called.</summary>
    public static string ChaseTitle(Quote quote) =>
        quote.Chased == 0
            ? $"Chase {quote.Customer} — {quote.What}"
            : $"Chase {quote.Customer} again — {quote.What}";

    /// <summary>"$4,207" — whole dollars, which is how a quote is spoken about.</summary>
    public static string Money(decimal amount) =>
        amount.ToString("C0", System.Globalization.CultureInfo.CurrentCulture);
}
