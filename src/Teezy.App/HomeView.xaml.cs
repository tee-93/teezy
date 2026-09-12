using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using System.Threading.Tasks;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.History;
using Teezy.Core.Mail;

namespace Teezy.App;


/// <summary>One line under the day band — a time and what is on.</summary>
public sealed record UpcomingRow(string When, string What, string Where, bool Done, bool Now)
{
    /// <summary>Past events stay on the card, faded, so the shape of the day is legible.</summary>
    public double Dim => Done ? 0.40 : 1.0;

    /// <summary>Struck through as well as faded — fade alone reads as "loading".</summary>
    public TextDecorationCollection? Strike => Done ? TextDecorations.Strikethrough : null;

    /// <summary>The one happening right now gets the marker; everything else is a plain rule.</summary>
    public double RailWidth => Now ? 4 : 3;
}

/// <summary>One unread message, as the inbox card shows it.</summary>
public sealed record InboxRow(
    string Initials, string Who, string Subject, string Mailbox, MailMessage Message);

/// <summary>The dashboard: the day said in one line, then the diary, the inbox and the figures.</summary>
/// <remarks>
/// Everything on it is a summary. The transcripts moved to their own page, so this one can
/// answer "what is going on" without also being a search results list.
/// </remarks>
public partial class HomeView : UserControl
{
    private readonly HistoryStore _history;
    private readonly CombinedCalendar? _calendar;
    private readonly CombinedMailbox? _mailbox;
    private IReadOnlyList<HistoryEntry> _all = [];

    /// <summary>When the day band was last filled, so opening Home does not re-read every time.</summary>
    /// <remarks>
    /// Reading a diary and a mailbox costs a network round trip and, for the mailbox, touches
    /// something private. Doing it on every navigation to Home would turn a glance at the
    /// history into a mail fetch, which is not what the user asked for by clicking Home.
    /// </remarks>
    private DateTimeOffset _summaryRead = DateTimeOffset.MinValue;

    private static readonly TimeSpan SummaryFreshFor = TimeSpan.FromMinutes(5);

    public HomeView(
        HistoryStore history,
        CombinedCalendar? calendar = null,
        CombinedMailbox? mailbox = null,
        Func<string>? hotkey = null)
    {
        InitializeComponent();
        _history = history;
        _calendar = calendar;
        _mailbox = mailbox;

        // The hotkey is the one thing on this page that is useful before you have read anything.
        HotkeyChip.Text = hotkey?.Invoke() ?? "";
        Refresh();
    }

    public void Refresh()
    {
        _all = _history.Load();
        
        UpdateStats();

        // Deliberately not awaited. The history is the page and must paint immediately; the
        // band fills in a moment later, or never, and neither delays anything.
        _ = UpdateSummaryAsync();
    }

    /// <summary>Fills the day band from whatever is connected.</summary>
    private async Task UpdateSummaryAsync()
    {
        var connectedDiary = _calendar is { IsConnected: true };
        var connectedMail = _mailbox is { IsConnected: true };

        if (!connectedDiary && !connectedMail)
        {
            SummaryBand.Visibility = Visibility.Collapsed;
            return;
        }

        if (DateTimeOffset.Now - _summaryRead < SummaryFreshFor) return;

        var now = DateTimeOffset.Now;

        // Midnight to midnight, not the spoken window. CalendarAnswer.Window starts at "now"
        // because nobody asks out loud about a meeting they have already sat through — but the
        // card shows the morning greyed out, and it cannot show what was never fetched.
        var midnight = now.ToLocalTime().Date;
        var from = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        var to = from.AddDays(1);

        var (since, atMost) = MailAnswer.Window(MailAsk.Unread, now);

        // Both at once, and neither allowed to take the other down: a mailbox that cannot be
        // reached must not cost the diary its half of the line.
        var diary = connectedDiary ? await Read(() => _calendar!.BetweenAsync(from, to)) : null;
        var mail = connectedMail ? await Read(() => _mailbox!.RecentAsync(since, atMost)) : null;

        _summaryRead = DateTimeOffset.Now;

        SummaryText.Text = DaySummary.For(diary, mail, now);
        SummaryBand.Visibility = Visibility.Visible;

        ShowToday(diary, now);
        ShowInbox(mail);
    }

    /// <summary>The whole day, with what has already happened dimmed rather than dropped.</summary>
    /// <remarks>
    /// The spoken answer only reports what is left, because nobody asks out loud to be told
    /// about a meeting they have already sat through. A card is read differently: seeing the
    /// morning greyed out is what makes an empty afternoon legible as an afternoon rather than
    /// as a calendar that failed to load.
    /// </remarks>
    private void ShowToday(CalendarReading? diary, DateTimeOffset now)
    {
        if (diary is null)
        {
            TodayCard.Visibility = Visibility.Collapsed;
            return;
        }

        var all = diary.Events;
        var left = all.Count(e => e.IsAllDay || e.End > now);

        TodayList.ItemsSource = all
            .Take(6)
            .Select(e => new UpcomingRow(
                e.IsAllDay ? "all day" : Spoken.Clock(e.Start),
                e.Subject,
                Where(e),
                Done: !e.IsAllDay && e.End <= now,
                Now: e.IsHappeningAt(now)))
            .ToList();

        TodayCount.Text = (left, all.Count) switch
        {
            (0, 0) => "",
            (0, var had) => $"{Plural(had, "thing")}, all done",
            var (remaining, had) when remaining == had => Plural(had, "thing"),
            var (remaining, had) => $"{remaining} left of {had}",
        };

        TodayEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TodayCard.Visibility = Visibility.Visible;
    }

    private void ShowInbox(MailReading? mail)
    {
        if (mail is null)
        {
            InboxCard.Visibility = Visibility.Collapsed;
            return;
        }

        var unread = mail.Messages.Where(m => m.IsUnread).ToList();

        InboxList.ItemsSource = unread.Take(5).Select(Row).ToList();

        InboxCount.Text = unread.Count == 0 ? "" : Plural(unread.Count, "unread", plural: "unread");
        InboxEmpty.Visibility = unread.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxCard.Visibility = Visibility.Visible;
    }

    private static InboxRow Row(MailMessage message) => new(
        Initials(message.Who),
        message.Who,
        message.Subject,
        message.Source is MailSource.Google ? "GMAIL" : "OUTLOOK",
        message);

    // ---- one message, read in place ----

    /// <summary>Opens the picked message in its own window.</summary>
    /// <remarks>
    /// <para>
    /// A real window rather than a panel laid over the page. A message is a thing you read and
    /// then dismiss, not a mode the dashboard enters, and a dialog gets the Escape key, the
    /// title bar and the focus trap for nothing.
    /// </para>
    /// <para>
    /// Selection, not a click, because the list is a <see cref="ListBox"/> — see the comment on
    /// it for why. The selection is cleared straight afterwards: it exists only to carry which
    /// row was picked, and a row still highlighted when the reader closes would be describing a
    /// state the page is no longer in. Clearing re-enters this handler with nothing selected,
    /// which the first line returns on.
    /// </para>
    /// </remarks>
    private void OnMessageOpened(object sender, SelectionChangedEventArgs e)
    {
        if (InboxList.SelectedItem is not InboxRow row) return;

        InboxList.SelectedItem = null;

        new MessageWindow(row.Message, row.Mailbox)
        {
            Owner = Window.GetWindow(this),
        }.ShowDialog();
    }

    /// <summary>Where a meeting is, when the organiser said and it is short enough to read.</summary>
    /// <remarks>
    /// Locations are frequently a whole conference URL. One of those in a card is a wall of
    /// characters that says nothing, so anything that long is dropped rather than trimmed.
    /// </remarks>
    private static string Where(CalendarEvent occurrence) =>
        occurrence.Location is { Length: > 0 and < 40 } place ? place : "";

    /// <summary>Two letters for the avatar, from the sender's own name.</summary>
    private static string Initials(string who)
    {
        var words = who.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}",
        };
    }

    private static string Plural(int count, string one, string? plural = null) =>
        count == 1 ? $"1 {one}" : $"{count} {plural ?? one + "s"}";

    /// <summary>Runs a read, turning any failure into the reading's own "unavailable" shape.</summary>
    /// <remarks>
    /// The combined readers already absorb one account failing; this catches the case where the
    /// whole call throws, so a broken connection greys one clause rather than the window.
    /// </remarks>
    private static async Task<T?> Read<T>(Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception e) when (e is CalendarUnavailableException or MailUnavailableException)
        {
            return null;
        }
    }


    private void UpdateStats()
    {
        var stats = UsageStats.From(_all, DateOnly.FromDateTime(DateTime.Today));

        Greeting.Text = $"Welcome back, {FirstName()}";
        Subtitle.Text = stats.TotalDictations == 0
            ? "Hold your push-to-talk key anywhere to start."
            : $"{stats.TotalDictations:N0} dictations so far.";

        TotalWords.Text = Compact(stats.TotalWords);
        Wpm.Text = stats.WordsPerMinute.ToString(CultureInfo.CurrentCulture);
        Streak.Text = stats.CurrentStreak.ToString(CultureInfo.CurrentCulture);
        StreakCaption.Text = stats.CurrentStreak == 1 ? "day streak" : "day streak";

        var minutes = Math.Max(0, stats.MinutesSavedVsTyping);
        TimeSaved.Text = minutes >= 60
            ? $"{minutes / 60:0.#} h"
            : $"{minutes:0} min";
    }

    private static string FirstName()
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name)) return "there";

        // Windows account names are rarely a bare first name: "ada_", "ada.lovelace" and
        // "ada-l" should all greet the same person.
        var cut = name.Split('.', '_', ' ', '-')[0];
        return cut.Length switch
        {
            0 => "there",
            1 => cut.ToUpperInvariant(),
            _ => char.ToUpperInvariant(cut[0]) + cut[1..],
        };
    }

    /// <summary>48,273 reads better as 48.3K in a small card.</summary>
    private static string Compact(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString("N0", CultureInfo.CurrentCulture),
    };
}
