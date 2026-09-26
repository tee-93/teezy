using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.Home;
using Teezy.Core.Mail;
using Teezy.Core.Meetings;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Home ▸ the two columns of panels.</summary>
/// <remarks>
/// <para>
/// Each panel is a card with a header that folds it away, remembered per panel. Panels grow to
/// fit what they hold, up to a sensible number of rows, with "N more" beyond — the page scrolls
/// as a whole, so nothing is trapped in a small scrolling box.
/// </para>
/// <para>
/// <b>No panel is ever blank.</b> An empty panel says what would go there and how to put it there.
/// </para>
/// </remarks>
public partial class HomeView
{
    private const int MaxRows = 8;

    private void RenderPanels(HomeSnapshot snapshot, TeezySettings settings)
    {
        LeftColumn.Children.Clear();
        RightColumn.Children.Clear();

        foreach (var key in HomeLayout.Sanitise(settings.HomeLeft, HomeLayout.LeftPanels, HomeLayout.DefaultLeft, CalendarConnected, MailConnected))
        {
            if (Build(key, snapshot) is { } panel) LeftColumn.Children.Add(panel);
        }

        foreach (var key in HomeLayout.Sanitise(settings.HomeRight, HomeLayout.RightPanels, HomeLayout.DefaultRight, CalendarConnected, MailConnected))
        {
            if (Build(key, snapshot) is { } panel) RightColumn.Children.Add(panel);
        }
    }

    private Border? Build(string key, HomeSnapshot s) => key switch
    {
        "today" => TodayPanel(s),
        "week" => WeekPanel(s),
        "coming_up" => ComingUpPanel(s),
        "quotes" => QuotesPanel(s),
        "notes" => NotesPanel(s),
        "meetings" => MeetingsPanel(s),
        "inbox" => InboxPanel(),
        _ => null,
    };

    // ============================== Today ==============================

    /// <summary>The day on one timeline: late first, then what has a time, then the rest of today.</summary>
    /// <summary>Today's email drop box, lit while an email is dragged over the panel.</summary>
    private DropZone? _todayZone;

    private Border TodayPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        body.Children.Add(QuickAdd());
        _todayZone = new DropZone { Resting = "Drag an email from Outlook here to make a task for today", Margin = new Thickness(0, 6, 0, 8) };
        body.Children.Add(_todayZone);

        var open = s.Tasks.Where(t => t.IsOpen).ToList();
        var late = open.Where(t => TaskPlan.BucketOf(t, s.Date) == TaskBucket.Overdue).OrderBy(t => t.Due).ToList();
        var today = open.Where(t => TaskPlan.BucketOf(t, s.Date) == TaskBucket.Today).ToList();

        // Anything with a time today: tasks due at a time, reminders set for today, and meetings.
        var timed = new List<(DateTime At, FrameworkElement Row)>();
        foreach (var task in today.Concat(open.Where(t => !today.Contains(t) && !late.Contains(t) && RemindsToday(t, s))).Distinct())
        {
            if (TimeToday(task, s) is { } at) timed.Add((at, TaskRow(task, s, Clock(at), late: false)));
        }

        if (s.Today is { } events)
        {
            foreach (var e in events)
            {
                var at = e.IsAllDay ? s.Date.ToDateTime(TimeOnly.MinValue) : e.Start.LocalDateTime;
                timed.Add((at, EventRow(e, s.Now)));
            }
        }

        var anyTime = today.Where(t => TimeToday(t, s) is null).ToList();

        if (late.Count > 0)
        {
            body.Children.Add(Label($"LATE  {late.Count}", Brand.Brush("CautionBorder")));
            foreach (var task in late.Take(MaxRows)) body.Children.Add(TaskRow(task, s, $"{(s.Date.DayNumber - task.Due!.Value.DayNumber)}d late", late: true));
            if (late.Count > MaxRows) body.Children.Add(More(late.Count - MaxRows));
        }

        if (timed.Count > 0)
        {
            body.Children.Add(Label("TODAY", Brand.Muted));
            foreach (var (_, row) in timed.OrderBy(t => t.At)) body.Children.Add(row);
        }

        if (anyTime.Count > 0)
        {
            body.Children.Add(Label("ANY TIME TODAY", Brand.Muted));
            foreach (var task in anyTime.Take(MaxRows)) body.Children.Add(TaskRow(task, s, string.Empty, late: false));
            if (anyTime.Count > MaxRows) body.Children.Add(More(anyTime.Count - MaxRows));
        }

        if (late.Count + timed.Count + anyTime.Count == 0)
        {
            body.Children.Add(Empty("Nothing booked today", CalendarConnected
                ? "No tasks due and nothing in your calendar. Add a task above, or drag an email in."
                : "No tasks due. Add one above, or drag an email from Outlook onto this panel."));
        }

        var count = late.Count + today.Count;
        var panel = Card("today", "TODAY", count == 0 ? null : $"{count} to do", body);

        // Emails dropped here become tasks for today.
        panel.AllowDrop = true;
        panel.PreviewDragOver += OnTaskDragOver;
        panel.PreviewDrop += OnTaskDrop;
        panel.PreviewDragLeave += (_, e) =>
        {
           
            if (DropZone.StillOver(panel, e)) _todayZone?.MaybeLeft();
            else _todayZone?.Rest();
        };
        return panel;
    }

    private static bool RemindsToday(TaskItem task, HomeSnapshot s) =>
        task.Remind is { } at && DateOnly.FromDateTime(at.LocalDateTime) == s.Date;

    /// <summary>When a task sits on today's timeline: its due time today, else a reminder today.</summary>
    private static DateTime? TimeToday(TaskItem task, HomeSnapshot s)
    {
        if (task.Due == s.Date && task.DueTime is { } time) return s.Date.ToDateTime(time);
        if (RemindsToday(task, s)) return task.Remind!.Value.LocalDateTime;
        return null;
    }

    private static string Clock(DateTime at) => TasksView.Clock(TimeOnly.FromDateTime(at));

    // ============================== This week ==============================

    /// <summary>Monday to Sunday: what is due each day, and meetings where a calendar is connected.</summary>
    private Border WeekPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        var total = 0;

        for (var i = 0; i < 7; i++)
        {
            var day = s.WeekStart.AddDays(i);
            var past = day < s.Date;
            var tasks = s.Tasks.Where(t => t.IsOpen && t.Due == day).OrderBy(t => t.DueTime ?? TimeOnly.MaxValue).ToList();
            var events = _week is { } week && CalendarConnected ? CalendarWeek.On(week.Events, day) : [];
            total += tasks.Count + events.Count;

            var entries = new StackPanel();
            foreach (var e in events)
            {
                entries.Children.Add(Line(e.IsAllDay ? "all day" : Spoken.Clock(e.Start), e.Subject, Brand.Brush("Info"), null));
            }
            foreach (var task in tasks)
            {
                entries.Children.Add(Line(task.DueTime is { } t ? TasksView.Clock(t) : "task", task.Title, Brand.Accent, task.Id));
            }
            if (entries.Children.Count == 0)
            {
                entries.Children.Add(new TextBlock { Text = past ? "—" : "Nothing due", FontSize = 12, Foreground = Brand.Faint });
            }

            var isToday = day == s.Date;
            var name = new StackPanel { Width = 64 };
            name.Children.Add(new TextBlock { Text = isToday ? "Today" : day.ToString("ddd", Display), FontWeight = FontWeights.SemiBold, Foreground = isToday ? Brand.Brush("AccentInk") : Brand.Ink });
            name.Children.Add(new TextBlock { Text = day.ToString("d MMM", Display), FontSize = 12, Foreground = Brand.Muted });

            var row = new Grid { Opacity = past ? 0.45 : 1 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(entries, 1);
            row.Children.Add(name);
            row.Children.Add(entries);

            body.Children.Add(new Border
            {
                BorderBrush = Brand.Brush("Hairline"),
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 8),
                Child = row,
            });
        }

        var end = s.WeekStart.AddDays(6);
        var span = s.WeekStart.Month == end.Month
            ? $"{s.WeekStart.Day}–{end.Day} {end.ToString("MMMM", Display)}"
            : $"{s.WeekStart.ToString("d MMM", Display)} – {end.ToString("d MMM", Display)}";
        return Card("week", "THIS WEEK", total == 0 ? span : $"{total} · {span}", body);
    }

    /// <summary>One entry on a day: a coloured tick of a line, when, and what.</summary>
    private FrameworkElement Line(string when, string what, Brush colour, string? taskId)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(2), Background = colour, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var time = new TextBlock { Text = when, FontSize = 12, Foreground = Brand.Muted };
        var text = new TextBlock { Text = what, FontSize = 12, Foreground = Brand.Brush("Body"), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(time, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(bar);
        grid.Children.Add(time);
        grid.Children.Add(text);

        if (taskId is not null) Clickable(grid, () => _actions.OpenTask(taskId));
        return grid;
    }

    // ============================== Coming up ==============================

    private Border ComingUpPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        var soon = s.Tasks
            .Where(t => t.IsOpen && t.Due is { } d && d > s.Date && d <= s.Date.AddDays(7))
            .OrderBy(t => t.Due).ThenBy(t => t.DueTime ?? TimeOnly.MaxValue)
            .ToList();

        foreach (var task in soon.Take(MaxRows))
        {
            var when = HomeTilesDay(task.Due!.Value, s.Date) + (task.DueTime is { } t ? $" {TasksView.Clock(t)}" : string.Empty);
            body.Children.Add(TaskRow(task, s, when, late: false));
        }

        if (soon.Count > MaxRows) body.Children.Add(More(soon.Count - MaxRows));
        if (soon.Count == 0) body.Children.Add(Empty("Nothing in the next week", "Tasks due in the next seven days show here."));

        return Card("coming_up", "COMING UP", soon.Count == 0 ? null : $"{soon.Count} this week", body);
    }

    // ============================== Quotes ==============================

    /// <summary>What wants chasing, and what has gone quiet — the two states worth acting on.</summary>
    private Border QuotesPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        var chase = Teezy.Core.Quotes.QuotePlan.DueToChase(s.AllQuotes, s.Date);
        var quiet = s.AllQuotes.Where(q => Teezy.Core.Quotes.QuotePlan.IsQuiet(q, s.Date)).ToList();

        if (chase.Count > 0)
        {
            body.Children.Add(Label($"TO CHASE  {chase.Count}", Brand.Brush("CautionBorder")));
            foreach (var quote in chase.Take(MaxRows)) body.Children.Add(QuoteRow(quote, late: true));
            if (chase.Count > MaxRows) body.Children.Add(More(chase.Count - MaxRows, Page.Quotes));
        }

        if (quiet.Count > 0)
        {
            body.Children.Add(Label($"GONE QUIET  {quiet.Count}", Brand.Muted));
            foreach (var quote in quiet.Take(MaxRows)) body.Children.Add(QuoteRow(quote, late: false));
        }

        if (chase.Count + quiet.Count == 0)
        {
            body.Children.Add(s.AllQuotes.Any(q => q.IsOpen)
                ? Empty("Nothing to chase", "Every quote out there has been chased recently.")
                : Empty("No quotes out", "Add one on the Quotes page and its chases book themselves."));
        }

        var open = Teezy.Core.Quotes.QuotePlan.Totals(s.AllQuotes, s.Date).Open;
        return Card("quotes", "QUOTES",
            open.Count == 0 ? null : Teezy.Core.Quotes.QuotePlan.Money(open.Value) + " out", body);
    }

    private Border QuoteRow(Teezy.Core.Quotes.Quote quote, bool late)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = quote.What.Length > 0 ? $"{quote.Customer} — {quote.What}" : quote.Customer,
            Foreground = Brand.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var money = new TextBlock
        {
            Text = Teezy.Core.Quotes.QuotePlan.Money(quote.Amount),
            FontSize = 12,
            Foreground = late ? Brand.Brush("CautionBorder") : Brand.Muted,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(money, 1);
        grid.Children.Add(text);
        grid.Children.Add(money);

        var row = new Border { Padding = new Thickness(0, 5, 0, 5), Child = grid, Background = System.Windows.Media.Brushes.Transparent };
        return Clickable(row, () => _actions.OpenPage(Page.Quotes));
    }

    private static string HomeTilesDay(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        1 => "Tomorrow",
        _ => day.ToString("ddd d MMM", Display),
    };

    // ============================== Recent notes ==============================

    /// <summary>The latest notes across every task — yours first in weight, TeezyFlow's quieter.</summary>
    private Border NotesPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        var recent = s.Tasks
            .SelectMany(t => t.Notes.Select(n => (Task: t, Note: n)))
            .Where(x => !x.Note.IsFromApp)
            .OrderByDescending(x => x.Note.At)
            .Take(6)
            .ToList();

        foreach (var (task, note) in recent)
        {
            var who = note.By ?? "Note";
            var head = new TextBlock { FontSize = 12 };
            head.Inlines.Add(new System.Windows.Documents.Run(who) { FontWeight = FontWeights.SemiBold, Foreground = Brand.Ink });
            head.Inlines.Add(new System.Windows.Documents.Run("  " + NoteWhen(note.At, s)) { Foreground = Brand.Faint });

            var text = new TextBlock
            {
                Text = note.Text.Replace('\n', ' '),
                FontSize = 12,
                Foreground = Brand.Brush("Body"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 2),
            };
            var on = new TextBlock { Text = $"on {task.Title}", FontSize = 11, Foreground = Brand.Muted, TextTrimming = TextTrimming.CharacterEllipsis };

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(text);
            stack.Children.Add(on);

            var avatar = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brand.Brush("AccentSoft"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = who[..1].ToUpper(Display), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brand.Brush("AccentInk"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(stack, 1);
            row.Children.Add(avatar);
            row.Children.Add(stack);
            body.Children.Add(Clickable(row, () => _actions.OpenTask(task.Id)));
        }

        if (recent.Count == 0) body.Children.Add(Empty("No notes yet", "Notes you add to a task show here, newest first."));
        return Card("notes", "RECENT NOTES", null, body);
    }

    private static string NoteWhen(DateTimeOffset at, HomeSnapshot s)
    {
        var local = at.LocalDateTime;
        var day = DateOnly.FromDateTime(local);
        var clock = TasksView.Clock(TimeOnly.FromDateTime(local));
        return (s.Date.DayNumber - day.DayNumber) switch
        {
            0 => $"today {clock}",
            1 => $"yesterday {clock}",
            _ => local.ToString("ddd d MMM", Display),
        };
    }

    // ============================== Meetings ==============================

    private Border MeetingsPanel(HomeSnapshot s)
    {
        var body = new StackPanel();
        var meetings = Meetings().Take(5).ToList();

        foreach (var meeting in meetings)
        {
            var started = meeting.Info.Started.LocalDateTime;
            var state = meeting.HasNotes ? "Notes ready" : meeting.HasTranscript ? "Transcript ready" : meeting.HasAudio ? "Waiting to transcribe" : "Recorded";
            var length = meeting.Info.Recorded.TotalMinutes >= 1 ? $" · {Math.Round(meeting.Info.Recorded.TotalMinutes)} min" : string.Empty;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = started.ToString("ddd d MMM, h:mm tt", Display), Foreground = Brand.Ink });
            stack.Children.Add(new TextBlock
            {
                Text = state + length,
                FontSize = 12,
                Foreground = meeting.HasNotes ? Brand.Brush("AccentInk") : Brand.Muted,
            });

            body.Children.Add(Clickable(new Border { Padding = new Thickness(0, 0, 0, 8), Child = stack }, () => _actions.OpenPage(Page.Meetings)));
        }

        if (meetings.Count == 0)
        {
            body.Children.Add(Empty("No meetings recorded", "Record a Teams call on the Meetings page and its notes land here."));
        }

        return Card("meetings", "MEETINGS", null, body);
    }

    // ============================== Inbox ==============================

    private Border? InboxPanel()
    {
        if (!MailConnected) return null;

        var body = new StackPanel();
        if (_mail is null)
        {
            body.Children.Add(Empty("Reading your inbox…", "Unread email shows here."));
            return Card("inbox", "INBOX", null, body);
        }

        var unread = _mail.Messages.Where(m => m.IsUnread).OrderByDescending(m => m.Received).ToList();
        foreach (var message in unread.Take(MaxRows))
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = message.Who, Foreground = Brand.Ink, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = message.Subject, FontSize = 12, Foreground = Brand.Brush("Body"), TextTrimming = TextTrimming.CharacterEllipsis });

            var mailbox = message.Source is MailSource.Google ? "GMAIL" : "OUTLOOK";
            body.Children.Add(Clickable(new Border { Padding = new Thickness(0, 0, 0, 8), Child = stack },
                () => new MessageWindow(message, mailbox) { Owner = Window.GetWindow(this) }.ShowDialog()));
        }

        if (unread.Count > MaxRows) body.Children.Add(More(unread.Count - MaxRows, Page.Home));
        if (unread.Count == 0) body.Children.Add(Empty("Nothing unread", "New email shows here."));

        return Card("inbox", "INBOX", unread.Count == 0 ? null : $"{unread.Count} unread", body);
    }

    // ============================== rows ==============================

    /// <summary>A task: tick to close, the title, and what matters about it — when, category, notes.</summary>
    private FrameworkElement TaskRow(TaskItem task, HomeSnapshot s, string when, bool late)
    {
        var tick = new CheckBox
        {
            Style = (Style)FindResource("Tick"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            ToolTip = "Close",
        };
        tick.Checked += (_, _) => _tasks.Close(task.Id);

        var title = new TextBlock { Text = task.Title, Foreground = Brand.Ink, TextTrimming = TextTrimming.CharacterEllipsis };

        var meta = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (task.Category is { } category)
        {
            meta.Children.Add(new Border
            {
                Style = (Style)FindResource("Tag"),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = category, Style = (Style)FindResource("TagText") },
            });
        }

        var bits = new List<string>();
        if (task.FollowUpOf is not null) bits.Add("follow-up");
        if (task.Notes.Count(n => !n.IsFromApp) is var notes and > 0)
        {
            var latest = task.Notes.Last(n => !n.IsFromApp).Text.Replace('\n', ' ');
            bits.Add($"{(notes == 1 ? "1 note" : $"{notes} notes")} · {(latest.Length > 40 ? latest[..40] + "…" : latest)}");
        }
        if (task.AllEmails.Count > 0) bits.Add("email");
        if (bits.Count > 0)
        {
            meta.Children.Add(new TextBlock { Text = string.Join(" · ", bits), FontSize = 12, Foreground = Brand.Muted, VerticalAlignment = VerticalAlignment.Center });
        }

        var text = new StackPanel();
        text.Children.Add(title);
        if (meta.Children.Count > 0) text.Children.Add(meta);

        var time = new TextBlock
        {
            Text = when,
            FontSize = 12,
            Foreground = late ? Brand.Brush("CautionBorder") : Brand.Muted,
            Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(time, 2);
        grid.Children.Add(tick);
        grid.Children.Add(text);
        grid.Children.Add(time);

        var row = new Border { Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(4), Child = grid };
        return Clickable(row, () => _actions.OpenTask(task.Id), except: tick);
    }

    /// <summary>A meeting on today's timeline, faded once it is over.</summary>
    private FrameworkElement EventRow(CalendarEvent e, DateTimeOffset now)
    {
        var done = !e.IsAllDay && e.End <= now;
        var on = e.IsHappeningAt(now);

        var bar = new Border { Width = on ? 4 : 3, CornerRadius = new CornerRadius(2), Background = on ? Brand.Accent : Brand.Brush("Info"), Margin = new Thickness(5, 0, 14, 0) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = e.Subject, Foreground = Brand.Ink, TextTrimming = TextTrimming.CharacterEllipsis, TextDecorations = done ? TextDecorations.Strikethrough : null });
        text.Children.Add(new TextBlock
        {
            Text = e.Location is { Length: > 0 and < 40 } place ? $"Meeting · {place}" : "Meeting",
            FontSize = 12,
            Foreground = Brand.Muted,
        });

        var time = new TextBlock
        {
            Text = e.IsAllDay ? "all day" : on ? "now" : Spoken.Clock(e.Start),
            FontSize = 12,
            Foreground = on ? Brand.Brush("AccentInk") : Brand.Muted,
            Margin = new Thickness(10, 1, 0, 0),
        };

        var grid = new Grid { Opacity = done ? 0.45 : 1, Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(time, 2);
        grid.Children.Add(bar);
        grid.Children.Add(text);
        grid.Children.Add(time);
        return grid;
    }

    // ============================== the card ==============================

    /// <summary>A panel: a header that folds it away, remembered, and its body.</summary>
    private Border Card(string key, string title, string? count, FrameworkElement body)
    {
        var collapsed = _settings().HomeCollapsed.Contains(key);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        if (count is not null)
        {
            var countText = new TextBlock { Text = count, FontSize = 12, Foreground = Brand.Faint, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(countText, 1);
            header.Children.Add(countText);
        }

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SectionToggle"),
            Content = header,
            IsChecked = !collapsed,
            ToolTip = "Fold or unfold",
        };

        var content = new Border { Margin = new Thickness(0, 12, 0, 0), Child = body, Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
        toggle.Click += (_, _) =>
        {
            var open = toggle.IsChecked == true;
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            var settings = _settings();
            var folded = settings.HomeCollapsed.Where(k => k != key).ToList();
            if (!open) folded.Add(key);
            _save(settings with { HomeCollapsed = folded });
        };

        var stack = new StackPanel();
        stack.Children.Add(toggle);
        stack.Children.Add(content);

        return new Border { Style = (Style)FindResource("CardPanel"), Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    private TextBlock Label(string text, Brush colour) => new()
    {
        Text = text,
        Style = (Style)FindResource("SectionLabel"),
        Foreground = colour,
        Margin = new Thickness(0, 12, 0, 4),
    };

    private StackPanel Empty(string title, string hint)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brand.Brush("Body"), FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("FormHint") });
        return stack;
    }

    private Button More(int count, Page page = Page.Tasks)
    {
        var button = new Button { Content = $"{count} more", Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        button.Click += (_, _) => _actions.OpenPage(page);
        return button;
    }

    /// <summary>Hover and click for a row, ignoring clicks that land on <paramref name="except"/> (its tick box).</summary>
    private static T Clickable<T>(T element, Action open, FrameworkElement? except = null) where T : FrameworkElement
    {
        element.Cursor = Cursors.Hand;
        if (element is Border border)
        {
            border.Background = Brushes.Transparent;
            border.MouseEnter += (_, _) => border.Background = Brand.Brush("Raised");
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        }
        else if (element is Panel panel)
        {
            panel.Background = Brushes.Transparent;
        }

        element.MouseLeftButtonUp += (_, e) =>
        {
            if (except is not null && e.OriginalSource is DependencyObject source && Within(source, except)) return;
            open();
        };
        return element;
    }

    private static bool Within(DependencyObject child, DependencyObject parent)
    {
        for (var node = child; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == parent) return true;
        }
        return false;
    }

    // ============================== adding from Home ==============================

    private Border QuickAdd()
    {
        var placeholder = new TextBlock
        {
            Text = "Add a task — e.g. Chase Cessnock quote fri 2pm #Quotes",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var box = new TextBox { Style = (Style)FindResource("BareText") };
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { box.Clear(); return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var today = DateOnly.FromDateTime(DateTime.Today);
            var parsed = TaskInput.Parse(box.Text, today);
            if (parsed.Title.Length == 0) return;

            // Added from Home with no day, it is for today — that is what this panel shows.
            var due = parsed.Due ?? today;
            _tasks.Add(parsed.Title, _actions.MatchCategory(parsed.Category), due: due, dueTime: parsed.DueTime,
                remind: parsed.DueTime is { } time ? TaskPlan.At(due, time) : null);
        };

        var grid = new Grid();
        grid.Children.Add(placeholder);
        grid.Children.Add(box);
        return new Border { Style = (Style)FindResource("FieldBox"), Child = grid, Margin = new Thickness(0, 0, 0, 4) };
    }

    private void OnTaskDragOver(object sender, DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject over && FindBox(over) is not null
            && !e.Data.GetDataPresent("FileGroupDescriptorW") && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var can = EmailDrop.CanTake(e.Data);
        e.Effects = can ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (can) _todayZone?.Ready("Drop to make a task for today");
    }

    /// <summary>An email dropped on Today: a task for today, with the email attached.</summary>
    private async void OnTaskDrop(object sender, DragEventArgs e)
    {
       
        if (e.OriginalSource is DependencyObject over && FindBox(over) is not null
            && !e.Data.GetDataPresent("FileGroupDescriptorW") && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;

        var today = DateOnly.FromDateTime(DateTime.Today);
       
        var zone = _todayZone;
        zone?.Reading();
        var emails = await EmailDrop.ReadAsync(e.Data);
        zone?.Rest();
        foreach (var email in emails)
        {
            var made = _tasks.Add(email.TaskTitle, due: today);
            _tasks.AddEmail(made.Id, email.ToTaskEmail(DateTimeOffset.Now));
            _tasks.AddNote(made.Id, email.From is { Length: > 0 } from ? $"Created from an email from {from}" : "Created from an email", TaskNote.App);
        }
    }

    private static TextBox? FindBox(DependencyObject source)
    {
        for (var node = source; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBox box) return box;
        }
        return null;
    }
}
