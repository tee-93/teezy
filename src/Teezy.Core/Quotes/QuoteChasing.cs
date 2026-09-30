using Teezy.Core.Tasks;

namespace Teezy.Core.Quotes;

/// <summary>Keeps a chase booked in the task list for every quote that is still out.</summary>
/// <remarks>
/// <para>
/// The quote decides <i>when</i> — three days, a week, a fortnight, then monthly — and the task
/// list does the chasing, because everything that makes a chase actually happen is already
/// there: the reminder card, the focus list, Home and the morning briefing. Nothing new had to
/// learn about quotes.
/// </para>
/// <para>
/// <b>Sending is what starts the chase.</b> One follow-up is booked the day a quote goes out, so
/// nothing sent is silently forgotten. Closing it is a deliberate moment, not this reconciler's:
/// what happens next — another follow-up, what kind, and when — is asked in the app, not booked
/// straight from a fixed cadence. Win, lose or stop the quote and the chasing stops here too.
/// </para>
/// <para>
/// <see cref="Follow"/> is idempotent and safe to call whenever either list changes: it looks at
/// what is there and books, retitles or cancels only what is out of step. That is also what
/// makes sync harmless — a quote chased on the other computer arrives with its count already up,
/// and this simply agrees.
/// </para>
/// </remarks>
public sealed class QuoteChasing(
    QuoteStore quotes, TaskStore tasks, Func<DateTimeOffset>? now = null, Func<TeezySettings>? settings = null)
{
    /// <summary>The category chase tasks are filed under, so they can be filtered like any other.</summary>
    public const string Category = "Quotes";

    /// <summary>The hour a chase reminder pops: first thing, before the day fills up.</summary>
    private static readonly TimeOnly RemindAt = new(9, 0);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Brings the task list and the quotes back into step, both ways.</summary>
    /// <returns>How many chases were booked, counted for the tests.</returns>
    public int Follow()
    {
        var today = DateOnly.FromDateTime(_now().LocalDateTime);
        var booked = 0;

        foreach (var quote in quotes.Visible)
        {
            var chase = quote.ChaseTaskId is { Length: > 0 } id ? tasks.Find(id) : null;

            if (!quote.IsOpen)
            {
                // Won or lost: whatever was chasing it is done with, not left in today's list.
                if (chase is { IsOpen: true }) tasks.Close(chase.Id);
                if (quote.ChaseTaskId is not null) quotes.Chasing(quote.Id, null);
                continue;
            }

            if (chase is null)
            {
                // Booked once, right after sending, in case that task was deleted before ever
                // being closed. Once a follow-up has actually been closed (Chased > 0), someone
                // is deciding what comes next for this quote, and it is not second-guessed here.
                if (quote.Chased > 0) continue;
                if (Book(quote, today)) booked++;
                continue;
            }

            if (!chase.IsOpen)
            {
                // Ticked off: counted, and the pointer cleared. What comes next, if anything, is
                // asked for at the moment it is closed (see FollowUpDialog) — not rebooked here
                // from a fixed cadence, which is the very thing this rework replaces.
                var on = chase.Closed is { } closed
                    ? DateOnly.FromDateTime(closed.LocalDateTime)
                    : today;

                quotes.Chased(quote.Id, on);
                quotes.Chasing(quote.Id, null);
                continue;
            }

            // Open and booked: keep the day right if the quote's dates have since changed.
            if (Day(quote, today) is { } due && chase.Due != due)
            {
                tasks.Update(chase with { Due = due, Remind = TaskPlan.At(due, RemindAt), Reminded = null });
            }
        }

        CloseOrphans();
        return booked;
    }

    /// <summary>
    /// Closes chases left behind by a quote that has been deleted — here or on another computer.
    /// Without this a deleted quote would go on asking to be chased for ever.
    /// </summary>
    private void CloseOrphans()
    {
        foreach (var task in tasks.Visible)
        {
            if (task is not { IsOpen: true, QuoteId: { Length: > 0 } id }) continue;
            if (quotes.Find(id) is null) tasks.Close(task.Id);
        }
    }

    /// <summary>
    /// The day the chase belongs on. A quote entered late is chased today rather than on a day
    /// already gone: a task dated last week reads as a failure, and this one has not failed at
    /// anything. Used when booking <b>and</b> when re-timing, or the two would argue.
    /// </summary>
    private static DateOnly? Day(Quote quote, DateOnly today) =>
        QuotePlan.NextChase(quote) is { } due ? (due < today ? today : due) : null;

    /// <summary>Books the first chase as a task, and points the quote at it.</summary>
    private bool Book(Quote quote, DateOnly today)
    {
        if (Day(quote, today) is not { } day) return false;

        var kind = settings?.Invoke().FollowUpKinds.FirstOrDefault() ?? "Follow-up";

        var task = tasks.Add(
            QuotePlan.ChaseTitle(quote),
            Category,
            due: day,
            remind: TaskPlan.At(day, RemindAt));

        tasks.Update(task with { QuoteId = quote.Id, Kind = kind });
        quotes.Chasing(quote.Id, task.Id);
        return true;
    }
}
