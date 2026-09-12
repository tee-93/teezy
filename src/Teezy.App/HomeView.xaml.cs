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

/// <summary>One dictation, shaped for display.</summary>
public sealed record HistoryRow(string Time, string Text, string Meta, HistoryEntry Entry);

/// <summary>A day of dictations under one heading.</summary>
public sealed record HistoryGroup(string Header, IReadOnlyList<HistoryRow> Items);

/// <summary>One line under the day band — a time and what is on.</summary>
public sealed record UpcomingRow(string When, string What, string Where);

/// <summary>One unread message, as the inbox card shows it.</summary>
public sealed record InboxRow(string Initials, string Who, string Subject);

/// <summary>Recent dictations, newest first, with the headline stats alongside.</summary>
/// <remarks>
/// The point of this page is recovery: text is injected into another app, and if that app
/// ate it, mangled it, or the user simply wants it again, this is the only place it still
/// exists. That is why the list is the page rather than a panel on it.
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
        ApplyFilter(SearchBox.Text);
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
        var (from, to) = CalendarAnswer.Window(CalendarAsk.Today, now);
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

    private void ShowToday(CalendarReading? diary, DateTimeOffset now)
    {
        if (diary is null)
        {
            TodayCard.Visibility = Visibility.Collapsed;
            return;
        }

        var left = diary.Events.Where(e => e.IsAllDay || e.End > now).ToList();

        TodayList.ItemsSource = left
            .Take(5)
            .Select(e => new UpcomingRow(
                e.IsAllDay ? "all day" : Spoken.Clock(e.Start),
                e.Subject,
                Where(e)))
            .ToList();

        TodayCount.Text = (left.Count, diary.Events.Count) switch
        {
            // The empty line under it already says there is nothing; "0 things" beside it is
            // the same fact stated twice, once badly.
            (0, 0) => "",
            (0, var had) => $"{Plural(had, "thing")}, all done",
            var (now_, had) when now_ == had => Plural(now_, "thing"),
            var (now_, had) => $"{now_} left of {had}",
        };

        TodayEmpty.Visibility = left.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        InboxList.ItemsSource = unread
            .Take(4)
            .Select(m => new InboxRow(Initials(m.Who), m.Who, m.Subject))
            .ToList();

        InboxCount.Text = unread.Count == 0 ? "" : Plural(unread.Count, "unread", plural: "unread");
        InboxEmpty.Visibility = unread.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxCard.Visibility = Visibility.Visible;
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
        TimeSavedHint.Text =
            $"versus typing at {UsageStats.AssumedTypingWpm} wpm — an upper bound if you type quickly.";
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter(SearchBox.Text);
    }

    private void ApplyFilter(string? query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _all
            : [.. _all.Where(e => e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))];

        Groups.ItemsSource = Group(matches);

        var empty = matches.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!empty) return;

        var searching = !string.IsNullOrWhiteSpace(query);
        EmptyTitle.Text = searching ? "No matches" : "Nothing dictated yet";
        EmptyHint.Text = searching
            ? $"Nothing in your history contains “{query}”."
            : "Everything you dictate is kept here, so you can copy it again later.";
    }

    private static List<HistoryGroup> Group(IReadOnlyList<HistoryEntry> entries)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return [.. entries
            .GroupBy(e => DateOnly.FromDateTime(e.At.LocalDateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryGroup(
                HeaderFor(g.Key, today),
                [.. g.OrderByDescending(e => e.At).Select(ToRow)]))];
    }

    private static string HeaderFor(DateOnly day, DateOnly today)
    {
        if (day == today) return "TODAY";
        if (day == today.AddDays(-1)) return "YESTERDAY";

        // Within the last week the weekday is more useful than the date.
        if (today.DayNumber - day.DayNumber < 7)
            return day.ToDateTime(TimeOnly.MinValue).ToString("dddd", CultureInfo.CurrentCulture).ToUpperInvariant();

        return day.ToDateTime(TimeOnly.MinValue)
            .ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture)
            .ToUpperInvariant();
    }

    private static HistoryRow ToRow(HistoryEntry e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.App)) parts.Add(e.App);
        if (e.WordCount > 0) parts.Add($"{e.WordCount} words");
        if (e.Corrections > 0) parts.Add($"{e.Corrections} correction{(e.Corrections == 1 ? "" : "s")}");

        return new HistoryRow(
            e.At.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture).ToLowerInvariant(),
            e.Text,
            string.Join("  ·  ", parts),
            e);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HistoryRow row) return;
        try
        {
            Clipboard.SetText(row.Entry.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open. Nothing useful to do, and it is
            // certainly not worth an error dialog over a copy button.
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HistoryRow row) return;
        _history.Delete(row.Entry.Id);
        Refresh();
    }
}
