using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.History;
using Teezy.Core.Home;
using Teezy.Core.Mail;
using Teezy.Core.Meetings;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>What Home can ask the window to do.</summary>
/// <param name="MatchCategory">A typed #category in the list's spelling, added to the list if new.</param>
public sealed record HomeActions(Action<string> OpenTask, Action<Page> OpenPage, Func<string?, string?> MatchCategory, Action? ShowBriefing = null,
    Action? ShowFocus = null);

/// <summary>Home: the day's update — a greeting and a line, tiles, and two columns of panels.</summary>
/// <remarks>
/// <para>
/// <b>Local first.</b> Everything paints straight away from what is on this computer — tasks,
/// notes, meetings, dictation — so the page is full on the work laptop, which can connect no
/// accounts. Calendar and mail, where connected, are read in the background at most every five
/// minutes and folded in when they arrive; a slow or failed read never delays or blanks the page.
/// </para>
/// <para>
/// Rebuilt on every refresh rather than bound: the page is small, and building it from one
/// snapshot means the header, the tiles and the panels cannot disagree about the day.
/// </para>
/// </remarks>
public partial class HomeView : UserControl
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    private readonly HistoryStore _history;
    private readonly TaskStore _tasks;
    private readonly Teezy.Core.Quotes.QuoteStore? _quotes;
    private readonly CombinedCalendar? _calendar;
    private readonly CombinedMailbox? _mailbox;
    private readonly MeetingStore? _meetingStore;
    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _save;
    private readonly HomeActions _actions;

    /// <summary>The week's calendar (Monday to Sunday) and recent mail, as last read; null until read or when not connected.</summary>
    private CalendarReading? _week;
    private MailReading? _mail;
    private DateTimeOffset _accountsRead = DateTimeOffset.MinValue;
    private bool _reading;

    private static readonly TimeSpan AccountsFreshFor = TimeSpan.FromMinutes(5);

    public HomeView(
        HistoryStore history,
        TaskStore tasks,
        Func<TeezySettings> settings,
        Action<TeezySettings> save,
        HomeActions actions,
        Func<string>? hotkey = null,
        CombinedCalendar? calendar = null,
        CombinedMailbox? mailbox = null,
        MeetingStore? meetings = null,
        Teezy.Core.Quotes.QuoteStore? quotes = null)
    {
        InitializeComponent();
        _history = history;
        _tasks = tasks;
        _settings = settings;
        _save = save;
        _actions = actions;
        _calendar = calendar;
        _mailbox = mailbox;
        _meetingStore = meetings;
        _quotes = quotes;

        HotkeyChip.Text = hotkey?.Invoke() ?? string.Empty;
        BriefingButton.Visibility = actions.ShowBriefing is null ? Visibility.Collapsed : Visibility.Visible;
        FocusButton.Visibility = actions.ShowFocus is null ? Visibility.Collapsed : Visibility.Visible;

        // Tasks change from the Tasks page, reminders and sync; Home follows while it is showing.
        _tasks.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Render(); });
        _history.Added += _ => Dispatcher.BeginInvoke(() => { if (IsLoaded) Render(); });
        if (_quotes is not null) _quotes.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Render(); });

        Refresh();
    }

    private bool CalendarConnected => _calendar is { IsConnected: true };

    private bool MailConnected => _mailbox is { IsConnected: true };

    /// <summary>Paints from what is here now, then reads the accounts if they are due a read.</summary>
    public void Refresh()
    {
        Render();

        // Not awaited: the page is already painted, and the accounts fold in when they answer.
        _ = ReadAccountsAsync();
    }

    /// <summary>Builds the whole page from one snapshot.</summary>
    private void Render()
    {
        var snapshot = Snapshot();
        var settings = _settings();

        Greeting.Text = $"{DayBrief.Greeting(snapshot.Now)}, {settings.NoteAuthor}";
        DateLine.Text = snapshot.Now.LocalDateTime.ToString("dddd d MMMM", Display);
        BriefLine.Text = DayBrief.For(snapshot);

        RenderTiles(snapshot, settings);
        RenderPanels(snapshot, settings);
        if (CustomisePanel.Visibility == Visibility.Visible) RenderCustomise(settings);
    }

    private HomeSnapshot Snapshot()
    {
        var now = DateTimeOffset.Now;
        var usage = UsageStats.From(_history.Load(), DateOnly.FromDateTime(now.LocalDateTime));
        var meetings = Meetings().Select(m => m.Info.Started).ToList();

        var today = _week is { } week && CalendarConnected
            ? (IReadOnlyList<CalendarEvent>)CalendarWeek.On(week.Events, DateOnly.FromDateTime(now.LocalDateTime))
            : null;

        return new HomeSnapshot(now, _tasks.Visible, usage, meetings, today, MailConnected ? _mail : null,
            _quotes?.Visible);
    }

    private IReadOnlyList<MeetingRecord> Meetings()
    {
        try { return _meetingStore?.List() ?? []; }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return []; }
    }

    // ---- accounts, in the background ----

    private async Task ReadAccountsAsync()
    {
        if (_reading || (!CalendarConnected && !MailConnected)) return;
        if (DateTimeOffset.Now - _accountsRead < AccountsFreshFor) return;

        _reading = true;
        try
        {
            var now = DateTimeOffset.Now;

            // Monday to Sunday, the working week Home shows, midnight to midnight.
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var from = CalendarWeek.Midnight(monday);
            var to = CalendarWeek.Midnight(monday.AddDays(7));
            var (since, atMost) = MailAnswer.Window(MailAsk.Unread, now);

            // Neither allowed to take the other down: a mailbox that cannot be reached must not
            // cost the diary its part of the page.
            _week = CalendarConnected ? await Read(() => _calendar!.BetweenAsync(from, to)) : null;
            _mail = MailConnected ? await Read(() => _mailbox!.RecentAsync(since, atMost)) : null;
            _accountsRead = DateTimeOffset.Now;
        }
        finally
        {
            _reading = false;
        }

        if (IsLoaded) Render();
    }

    /// <summary>Runs a read, turning a failure into "not read" rather than an error on the page.</summary>
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

    private void OnBriefing(object sender, RoutedEventArgs e) => _actions.ShowBriefing?.Invoke();

    private void OnFocus(object sender, RoutedEventArgs e) => _actions.ShowFocus?.Invoke();

    // ---- the page's shape ----

    /// <summary>Two columns when there is room; the right one under the left when there is not.</summary>
    private void OnPageSized(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 900;
        LeftWidth.Width = new GridLength(narrow ? 1 : 13, GridUnitType.Star);
        GapWidth.Width = new GridLength(narrow ? 0 : 14);
        RightWidth.Width = narrow ? new GridLength(0) : new GridLength(7, GridUnitType.Star);
        Grid.SetColumn(RightColumn, narrow ? 0 : 2);
        Grid.SetRow(RightColumn, narrow ? 1 : 0);
        RightColumn.Margin = new Thickness(0, narrow ? 14 : 0, 0, 0);

        ArrangeTiles(e.NewSize.Width);
    }
}
