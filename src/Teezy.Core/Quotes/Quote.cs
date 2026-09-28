using Teezy.Core.Tasks;

namespace Teezy.Core.Quotes;

/// <summary>The currencies a quote can be in, and how each is written.</summary>
/// <remarks>
/// A short, fixed list rather than something typed freely or added to in Settings, the way a
/// quote type is: there is no meaningful "new currency" the way there is a new type of job, and
/// a typo here would silently mean the wrong money. AUD, USD and NZD all use the same symbol, so
/// anything not the default currency is written with its code too, or the amount would read as
/// the wrong money at a glance.
/// </remarks>
public static class Currencies
{
    public const string Default = "AUD";

    public static readonly IReadOnlyList<string> All = ["AUD", "USD", "EUR", "NZD"];

    /// <summary>The symbol for a currency, or its own code if it has none worth using.</summary>
    public static string Symbol(string currency) => currency switch
    {
        "EUR" => "€",
        "AUD" or "USD" or "NZD" => "$",
        _ => currency,
    };

    /// <summary>
    /// One of <see cref="All"/>, however it was cased or spaced — <c>null</c> and anything not
    /// recognised become <see cref="Default"/> rather than an invalid currency travelling on.
    /// </summary>
    public static string Clean(string? currency)
    {
        var typed = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return All.FirstOrDefault(c => c == typed) ?? Default;
    }
}

/// <summary>Where a quote stands.</summary>
/// <remarks>
/// The numbers matter: they are what gets written to disk, and quotes.json already has real
/// quotes in it. <see cref="Quoted"/>=0, <see cref="Won"/>=1 and <see cref="Lost"/>=2 are exactly
/// what this enum meant before it had a name for "not sent yet" — the old <c>Open</c> was always
/// a quote that had gone out, which is what <see cref="Quoted"/> means now. <see cref="InProgress"/>
/// is new, so it goes on the end rather than displacing anything.
/// </remarks>
public enum QuoteStatus
{
    Quoted = 0,
    Won = 1,
    Lost = 2,
    InProgress = 3,
}

/// <summary>Which pile a quote belongs in on the page.</summary>
public enum QuoteBucket
{
    /// <summary>Not sent yet — still being put together.</summary>
    Drafting,

    /// <summary>Its next chase is today or overdue.</summary>
    ToChase,

    /// <summary>Open, and nothing has happened on it for weeks.</summary>
    Quiet,

    /// <summary>Open, chased recently enough, nothing to do today.</summary>
    Open,

    /// <summary>Won or lost.</summary>
    Decided,
}

/// <summary>One quote, from the first draft to the day it is won or lost.</summary>
/// <param name="Id">Stable across computers, so sync can match the same quote on each.</param>
/// <param name="Name">What the quote is called, in a few words — the job, not the customer.</param>
/// <param name="Customer">Who it is for — the company, as you would say it out loud.</param>
/// <param name="Type">
/// One of the types in Settings ▸ Tasks, e.g. "Supply only". Null for none.
/// </param>
/// <param name="AmountCents">
/// The value, in whole cents. Null while it is still being worked out. Money is never a
/// <c>double</c> here: a quote is a number someone will be held to, and 4,207.35 typed into a
/// binary fraction stops being that number.
/// </param>
/// <param name="Sent">
/// The day it went out. Null while <see cref="Status"/> is <see cref="QuoteStatus.InProgress"/> —
/// chasing is counted from here, so nothing is chased until this is set.
/// </param>
/// <param name="Cadence">
/// Days after sending to chase on, copied from the settings when the quote is sent so that
/// changing the setting later does not silently re-time quotes already out.
/// </param>
/// <param name="Chased">How many chases have been done.</param>
/// <param name="LastChased">The day of the last one; null until the first.</param>
/// <param name="Status">In progress, quoted, won or lost.</param>
/// <param name="Decided">The day it was won or lost.</param>
/// <param name="Reference">Your own quote number, if it has one. What a CRM import matches on.</param>
/// <param name="Contact">The person at that customer.</param>
/// <param name="Notes">Running notes, oldest first — the same shape as a task's.</param>
/// <param name="Emails">Emails dropped onto the quote, kept apart from the notes.</param>
/// <param name="ChaseTaskId">The task doing the chasing now, so the two stay in step.</param>
/// <param name="Modified">When it last changed, anywhere. The newer copy wins when computers disagree.</param>
/// <param name="Deleted">Deleted, kept as a marker so the deletion reaches the other computers.</param>
/// <param name="Currency">
/// One of <see cref="Currencies.All"/>. Stamped from Settings ▸ Tasks when the quote is made, like
/// <see cref="Cadence"/>, so changing the default later leaves quotes already made in whatever
/// they were quoted in.
/// </param>
public sealed record Quote(
    string Id,
    string Name,
    string Customer,
    long? AmountCents,
    DateOnly? Sent,
    IReadOnlyList<int> Cadence,
    int Chased,
    DateOnly? LastChased,
    QuoteStatus Status,
    DateOnly? Decided,
    string? Reference,
    string? Contact,
    IReadOnlyList<TaskNote> Notes,
    DateTimeOffset Modified,
    string? Type = null,
    string Currency = Currencies.Default,
    IReadOnlyList<TaskEmail>? Emails = null,
    string? ChaseTaskId = null,
    bool Deleted = false)
{
    /// <summary>Not decided yet — either still being drafted, or out and awaiting an answer.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpen => Status is QuoteStatus.InProgress or QuoteStatus.Quoted && !Deleted;

    /// <summary>The attached emails, never null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TaskEmail> AllEmails => Emails ?? [];

    /// <summary>The value in dollars, for display and for adding up. Zero while it is not yet known.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Amount => (AmountCents ?? 0) / 100m;

    /// <summary>The last thing that happened: a chase, or the day it went out. Null while a draft.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly? LastMoved =>
        LastChased is { } chased && (Sent is null || chased > Sent) ? chased : Sent;

    public static Quote New(
        string customer,
        string name,
        long? amountCents,
        DateOnly? sent,
        DateTimeOffset now,
        IReadOnlyList<int>? cadence = null,
        string? reference = null,
        string? contact = null,
        string? type = null,
        string? currency = null) =>
        new(Guid.NewGuid().ToString("N"),
            name.Trim(),
            customer.Trim(),
            amountCents,
            sent,
            cadence is { Count: > 0 } ? [.. cadence] : QuotePlan.DefaultCadence,
            Chased: 0,
            LastChased: null,
            sent is null ? QuoteStatus.InProgress : QuoteStatus.Quoted,
            Decided: null,
            Clean(reference),
            Clean(contact),
            Notes: [],
            Modified: now,
            Type: Clean(type),
            Currency: Currencies.Clean(currency));

    internal static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>How much is out, what is due a chase, and what was won.</summary>
/// <param name="Drafting">How many quotes are still being put together, not sent yet.</param>
/// <param name="Open">
/// Open quotes in the default currency, and what they are worth. A quote in another currency is
/// still on the page in its own right money — it is left out here rather than added to a total
/// that would then be no single real currency.
/// </param>
/// <param name="Won">Won this month, and for how much — default currency, for the same reason.</param>
/// <param name="Lost">Lost this month, default currency.</param>
/// <param name="WinRate">Won over decided this month, 0 to 1; null when nothing was decided.</param>
/// <param name="OtherCurrencyOpen">Open quotes in another currency, not counted in <see cref="Open"/>.</param>
public sealed record QuoteTotals(
    int Drafting,
    (int Count, decimal Value) Open,
    (int Count, decimal Value) Won,
    (int Count, decimal Value) Lost,
    double? WinRate,
    int OtherCurrencyOpen = 0);

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

    /// <summary>The day the next chase falls on, or null once a quote is decided or not sent yet.</summary>
    public static DateOnly? NextChase(Quote quote)
    {
        if (quote.Status != QuoteStatus.Quoted || quote.Sent is not { } sent) return null;

        var from = quote.Chased < quote.Cadence.Count
            ? sent.AddDays(quote.Cadence[quote.Chased])
            : (quote.LastMoved ?? sent).AddDays(ThereafterDays);

        // Nobody reads a chasing email on a Sunday, and a follow-up that lands then is answered
        // on Monday anyway — so it is booked for the Monday.
        return TaskPlan.Workday(from);
    }

    /// <summary>Open, and nothing has happened on it for <see cref="QuietDays"/>.</summary>
    public static bool IsQuiet(Quote quote, DateOnly today) =>
        quote.Status == QuoteStatus.Quoted && !quote.Deleted
        && quote.LastMoved is { } moved && today.DayNumber - moved.DayNumber >= QuietDays;

    /// <summary>Where a quote belongs on the page today.</summary>
    public static QuoteBucket BucketOf(Quote quote, DateOnly today)
    {
        if (!quote.IsOpen) return QuoteBucket.Decided;
        if (quote.Status == QuoteStatus.InProgress) return QuoteBucket.Drafting;
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
                    ? g.OrderByDescending(q => q.Decided ?? q.Sent ?? DateOnly.MinValue)
                    : g.Key == QuoteBucket.Drafting
                        ? g.OrderByDescending(q => q.Modified)
                        : g.OrderBy(q => NextChase(q) ?? q.Sent ?? DateOnly.MaxValue).ThenBy(q => q.Sent),
            ])),
    ];

    /// <summary>Everything due a chase today or earlier, oldest first.</summary>
    public static IReadOnlyList<Quote> DueToChase(IEnumerable<Quote> quotes, DateOnly today) =>
        [.. quotes.Where(q => BucketOf(q, today) == QuoteBucket.ToChase)
            .OrderBy(q => NextChase(q) ?? DateOnly.MaxValue)];

    /// <summary>What is out, drafting or decided, and how the month that contains <paramref name="today"/> went.</summary>
    public static QuoteTotals Totals(IEnumerable<Quote> quotes, DateOnly today)
    {
        var all = quotes.Where(q => !q.Deleted).ToList();
        var drafting = all.Count(q => q.Status == QuoteStatus.InProgress);
        var open = all.Where(q => q.Status == QuoteStatus.Quoted).ToList();
        var openHome = open.Where(q => q.Currency == Currencies.Default).ToList();

        bool ThisMonth(Quote q) =>
            q.Decided is { } decided && decided.Year == today.Year && decided.Month == today.Month;

        // Count and value stay in step here — a "3 quotes, $2,000" tile whose $2,000 only
        // covered two of the three would read as one figure describing all of them.
        var won = all.Where(q => q.Status == QuoteStatus.Won && ThisMonth(q) && q.Currency == Currencies.Default).ToList();
        var lost = all.Where(q => q.Status == QuoteStatus.Lost && ThisMonth(q) && q.Currency == Currencies.Default).ToList();
        var decided = won.Count + lost.Count;

        return new QuoteTotals(
            drafting,
            (openHome.Count, openHome.Sum(q => q.Amount)),
            (won.Count, won.Sum(q => q.Amount)),
            (lost.Count, lost.Sum(q => q.Amount)),
            decided == 0 ? null : (double)won.Count / decided,
            OtherCurrencyOpen: open.Count - openHome.Count);
    }

    /// <summary>What the chase task for a quote is called.</summary>
    public static string ChaseTitle(Quote quote) =>
        quote.Chased == 0
            ? $"Chase {quote.Customer} — {quote.Name}"
            : $"Chase {quote.Customer} again — {quote.Name}";

    /// <summary>
    /// "$4,207" for the default currency, "$4,207 USD" for any other — AUD, USD and NZD share a
    /// symbol, so anything not the default is named as well or it would read as the wrong money.
    /// </summary>
    public static string Money(decimal amount, string currency = Currencies.Default)
    {
        var formatted = Currencies.Symbol(currency) + amount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        return currency == Currencies.Default ? formatted : $"{formatted} {currency}";
    }
}
