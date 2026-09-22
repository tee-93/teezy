using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Commands;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>TeezyFlow's own task list: quick add, grouped by when things are due, and a panel per task.</summary>
/// <remarks>
/// <para>
/// <b>Close and follow up</b> is the heart of it: a quote goes out, the task closes, and a
/// follow-up for a chosen day takes its place, linked back — so the whole story of a quote reads
/// as one chain in the panel rather than as unrelated lines.
/// </para>
/// <para>
/// <b>The AI sees an email only when a button is pressed</b>, and only the one pasted into the
/// panel. What comes back is text to read or copy. See <see cref="IMailAdvisor"/> for why the
/// request carries no tools.
/// </para>
/// </remarks>
public partial class TasksView : UserControl
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    private readonly TaskStore _store;
    private readonly IMailAdvisor? _advisor;
    private readonly Func<Teezy.Core.TeezySettings> _settings;
    private readonly Action<Teezy.Core.TeezySettings> _saveSettings;
    private string? _selected;
    private string? _category;
    private bool _showClosed;

    // What Undo puts back: the task closed, and the follow-up made with it, if any.
    private (string Closed, string? FollowUp)? _undo;

    internal TasksView(TaskStore store, IMailAdvisor? advisor,
        Func<Teezy.Core.TeezySettings> settings, Action<Teezy.Core.TeezySettings> saveSettings)
    {
        InitializeComponent();
        _store = store;
        _advisor = advisor;
        _settings = settings;
        _saveSettings = saveSettings;
        InitPanel();

        // Changes arrive from sync and reminders on other threads.
        _store.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Refresh(); });
        Loaded += (_, _) => Refresh();
    }

    /// <summary>
    /// The categories to offer: Settings ▸ Tasks, plus any a task has that the list lacks (one
    /// made on a computer with an older list, or since removed), so none is ever hidden.
    /// </summary>
    private IReadOnlyList<string> Categories(IReadOnlyList<TaskItem> all)
    {
        var listed = _settings().TaskCategories;
        var extra = TaskPlan.Categories(all).Where(c => !listed.Contains(c, StringComparer.OrdinalIgnoreCase));
        return [.. listed, .. extra];
    }

    /// <summary>
    /// A category typed in quick add, matched to the list's own spelling; one not yet on the
    /// list is added to it, so "#quotes" and the picker agree.
    /// </summary>
    private string? Category(string? typed)
    {
        if (typed is not { Length: > 0 }) return null;
        var settings = _settings();
        var match = settings.TaskCategories.FirstOrDefault(c => c.Equals(typed, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        _saveSettings(settings with { TaskCategories = [.. settings.TaskCategories, typed] });
        return typed;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Opens a task in the panel — from Home, or from a reminder.</summary>
    public void Select(string id)
    {
        _selected = id;
        _showClosed |= _store.Find(id) is { IsOpen: false };
        Refresh();
    }

    public void Refresh()
    {
        var all = _store.Visible;
        var today = Today;

        if (_category is not null && !Categories(all).Contains(_category, StringComparer.OrdinalIgnoreCase))
        {
            _category = null;
        }

        BuildFilters(all);
        BuildList(all, today);
        BuildClosed(all);
        ShowDetail(fields: !DetailHasFocus());
    }

    // ============================== quick add ==============================

    private void OnAddTyped(object sender, TextChangedEventArgs e)
    {
        AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var parsed = TaskInput.Parse(AddBox.Text, Today);
        var bits = new List<string>();
        if (parsed.Due is { } due) bits.Add($"Due {Day(due)}");
        if (parsed.DueTime is { } time) bits.Add($"reminder at {Clock(time)}");
        if ((parsed.Category ?? _category) is { } category) bits.Add(category);

        AddPreview.Text = bits.Count > 0 ? string.Join(" · ", bits) + "  —  Enter to add" : "Enter to add";
        AddPreview.Visibility = AddBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { AddBox.Clear(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var parsed = TaskInput.Parse(AddBox.Text, Today);
        if (parsed.Title.Length == 0) return;

        // A time with no day means today, and a time is a reminder too.
        var due = parsed.Due ?? (parsed.DueTime is not null ? Today : null);
        DateTimeOffset? remind = due is { } day && parsed.DueTime is { } time ? TaskPlan.At(day, time) : null;
        _store.Add(parsed.Title, Category(parsed.Category) ?? _category, due: due, dueTime: parsed.DueTime, remind: remind);
        AddBox.Clear();
    }

    // ============================== the list ==============================

    private void BuildFilters(IReadOnlyList<TaskItem> all)
    {
        Filters.Children.Clear();
        var categories = Categories(all);
        if (categories.Count == 0) return;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Segment("All", null));
        foreach (var category in categories) row.Children.Add(Segment(category, category));
        Filters.Children.Add(new Border { Style = Styled("SegmentGroup"), Child = row });

        RadioButton Segment(string label, string? category)
        {
            var open = all.Count(t => t.IsOpen && (category is null
                || string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)));
            var button = new RadioButton
            {
                Content = $"{label}  {open}",
                Style = Styled("Segment"),
                GroupName = "TaskFilter",
                IsChecked = string.Equals(_category, category, StringComparison.OrdinalIgnoreCase),
            };
            button.Checked += (_, _) =>
            {
                if (string.Equals(_category, category, StringComparison.OrdinalIgnoreCase)) return;
                _category = category;
                Dispatcher.BeginInvoke(Refresh);
            };
            return button;
        }
    }

    private void BuildList(IReadOnlyList<TaskItem> all, DateOnly today)
    {
        Groups.Children.Clear();
        var groups = TaskPlan.Arrange(all, today, _category);
        var open = groups.Sum(g => g.Tasks.Count);
        var due = groups.Where(g => g.Bucket is TaskBucket.Overdue or TaskBucket.Today).Sum(g => g.Tasks.Count);

        Lede.Text = open == 0 ? "Nothing open." : due == 0
            ? $"{open} open, nothing due today."
            : $"{open} open, {due} due today or overdue.";

        if (open == 0)
        {
            EmptyTitle.Text = _category is null ? "Nothing to do" : $"Nothing open in {_category}";
            EmptyText.Text = "Type a task above. Put a day at the end — fri, tomorrow, 25/9 — and a #category if you like.";
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        foreach (var (bucket, tasks) in groups)
        {
            Groups.Children.Add(new TextBlock
            {
                Text = $"{BucketName(bucket)}  {tasks.Count}",
                Style = Styled("SectionLabel"),
                Foreground = bucket == TaskBucket.Overdue ? Brush("CautionBorder") : Brush("Muted"),
                Margin = new Thickness(0, Groups.Children.Count == 0 ? 0 : 18, 0, 6),
            });

            var rows = new StackPanel();
            for (var i = 0; i < tasks.Count; i++) rows.Children.Add(Row(tasks[i], all, first: i == 0, today));
            Groups.Children.Add(new Border { Style = Styled("ListCard"), Child = rows });
        }
    }

    private static string BucketName(TaskBucket bucket) => bucket switch
    {
        TaskBucket.Overdue => "OVERDUE",
        TaskBucket.Today => "TODAY",
        TaskBucket.Upcoming => "UPCOMING",
        TaskBucket.NoDate => "NO DATE",
        _ => "NOT STARTED",
    };

    private FrameworkElement Row(TaskItem task, IReadOnlyList<TaskItem> all, bool first, DateOnly today)
    {
        var tick = new CheckBox
        {
            Style = Styled("Tick"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0),
            ToolTip = "Close",
            IsChecked = !task.IsOpen,
        };

        var title = new TextBlock
        {
            Text = task.Title,
            Foreground = task.IsOpen ? Brush("Ink") : Brush("Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = task.IsOpen ? null : TextDecorations.Strikethrough,
        };

        var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        if (task.Pinned && task.IsOpen)
        {
            meta.Children.Add(new Border
            {
                Style = Styled("Tag"),
                Background = Brush("AccentSoft"),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "On the focus list",
                Child = new TextBlock { Text = "Focus", Style = Styled("TagText"), Foreground = Brush("AccentInk") },
            });
        }

        if (task.Category is { } category)
        {
            meta.Children.Add(new Border
            {
                Style = Styled("Tag"),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = category, Style = Styled("TagText") },
            });
        }

        var words = MetaWords(task, all, today, out var late);
        if (words.Length > 0)
        {
            var text = new TextBlock
            {
                Text = words,
                FontSize = 12,
                Foreground = late ? Brush("CautionBorder") : Brush("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            meta.Children.Add(text);
        }

        var body = new StackPanel();
        body.Children.Add(title);
        if (meta.Children.Count > 0) body.Children.Add(meta);

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(body, 1);
        line.Children.Add(tick);
        line.Children.Add(body);

        var selected = task.Id == _selected;
        var row = new Border
        {
            // What a drop on this row is added to.
            Tag = task.Id,
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = selected ? Brush("Selected") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = line,
        };

        row.MouseEnter += (_, _) => { if (task.Id != _selected) row.Background = Brush("Raised"); };
        row.MouseLeave += (_, _) => { if (task.Id != _selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, tick)) return;
            _selected = task.Id;
            Refresh();
        };

        tick.Checked += (_, _) => { if (task.IsOpen) CloseTask(task.Id); };
        tick.Unchecked += (_, _) => { if (!task.IsOpen) _store.Reopen(task.Id); };
        return row;
    }

    /// <summary>The grey line under a task: when, how late, notes, and where it sits in a chain.</summary>
    private static string MetaWords(TaskItem task, IReadOnlyList<TaskItem> all, DateOnly today, out bool late)
    {
        late = false;
        var parts = new List<string>();

        if (!task.IsOpen && task.Closed is { } closed)
        {
            parts.Add($"Closed {Day(DateOnly.FromDateTime(closed.LocalDateTime))}");
        }
        else
        {
            switch (TaskPlan.BucketOf(task, today))
            {
                case TaskBucket.NotStarted:
                    parts.Add($"Starts {Day(task.Start!.Value)}");
                    if (task.Due is { } d) parts.Add($"due {Day(d)}");
                    break;
                case TaskBucket.Overdue:
                    late = true;
                    var days = today.DayNumber - task.Due!.Value.DayNumber;
                    parts.Add(days == 1 ? "Due yesterday" : $"{days} days late · was due {Day(task.Due.Value)}");
                    break;
                case TaskBucket.Today:
                    parts.Add(task.DueTime is { } t ? $"Today {Clock(t)}" : "Today");
                    break;
                case TaskBucket.Upcoming:
                    parts.Add(task.DueTime is { } at ? $"{Day(task.Due!.Value)} {Clock(at)}" : Day(task.Due!.Value));
                    break;
            }
        }

        if (task.Notes.Count > 0) parts.Add(task.Notes.Count == 1 ? "1 note" : $"{task.Notes.Count} notes");

        var chain = TaskPlan.Chain(all, task.Id);
        if (chain.Count > 1)
        {
            var step = chain.ToList().FindIndex(t => t.Id == task.Id) + 1;
            parts.Add(step == 1 ? $"followed up {chain.Count - 1}×" : $"follow-up {step - 1} of {chain.Count - 1}");
        }

        return string.Join(" · ", parts);
    }

    private void BuildClosed(IReadOnlyList<TaskItem> all)
    {
        var closed = all
            .Where(t => t.Closed is not null)
            .Where(t => _category is null || string.Equals(t.Category, _category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Closed)
            .Take(50)
            .ToList();

        ClosedToggle.Visibility = closed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClosedToggle.Content = _showClosed ? "Hide closed" : $"Show closed ({closed.Count}{(closed.Count == 50 ? "+" : string.Empty)})";
        ClosedList.Visibility = _showClosed && closed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ClosedList.Children.Clear();
        if (!_showClosed || closed.Count == 0) return;

        var rows = new StackPanel();
        for (var i = 0; i < closed.Count; i++) rows.Children.Add(Row(closed[i], all, first: i == 0, Today));
        ClosedList.Children.Add(new Border { Style = Styled("ListCard"), Child = rows });
    }

    private void OnToggleClosed(object sender, RoutedEventArgs e)
    {
        _showClosed = !_showClosed;
        Refresh();
    }

    // ============================== closing ==============================

    private void CloseTask(string id)
    {
        if (_store.Find(id) is not { } task) return;
        _store.Close(id);
        OfferUndo($"Closed: {task.Title}", id, null);
    }

    private void FollowUp(DateOnly due)
    {
        if (_selected is not { } id || _store.Find(id) is not { IsOpen: true } task) return;

        var next = _store.CloseAndFollowUp(id, due, dueTime: task.DueTime);
        _selected = next.Id;
        FollowUpDay.Set(null, null);
        OfferUndo($"Closed, and following up {Day(due)}: {task.Title}", id, next.Id);
        Refresh();
    }

    private void OfferUndo(string text, string closed, string? followUp)
    {
        _undo = (closed, followUp);
        UndoText.Text = text;
        UndoBar.Visibility = Visibility.Visible;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        UndoBar.Visibility = Visibility.Collapsed;
        if (_undo is not { } undo) return;
        _undo = null;

        if (undo.FollowUp is { } made) _store.UndoFollowUp(undo.Closed, made);
        else _store.Reopen(undo.Closed);
        _selected = undo.Closed;
        Refresh();
    }

    private void OnCloseTask(object sender, RoutedEventArgs e)
    {
        if (_selected is { } id) CloseTask(id);
    }

    private void OnReopen(object sender, RoutedEventArgs e)
    {
        if (_selected is { } id) _store.Reopen(id);
    }

    private void BuildFollowUpChoices()
    {
        FollowUpChoices.Children.Clear();
        var today = Today;
        (string Label, DateOnly Day)[] choices =
        [
            ("Tomorrow", TaskPlan.Workday(today.AddDays(1))),
            ("In 3 days", TaskPlan.Workday(today.AddDays(3))),
            ("In a week", TaskPlan.Workday(today.AddDays(7))),
            ("In 2 weeks", TaskPlan.Workday(today.AddDays(14))),
        ];

        foreach (var (label, day) in choices)
        {
            var button = new Button
            {
                Content = $"{label} · {day.ToString("ddd d", Display)}",
                Style = Styled("Secondary"),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = $"Close this and add a follow-up for {Day(day)}",
            };
            button.Click += (_, _) => FollowUp(day);
            FollowUpChoices.Children.Add(button);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } id) return;

        // Two presses, so a stray click costs nothing.
        if (DeleteButton.Tag is not "armed")
        {
            DeleteButton.Tag = "armed";
            DeleteButton.Content = "Press again to delete";
            return;
        }

        _store.Delete(id);
        _selected = null;
        Refresh();
    }

    // ============================== emails dragged in ==============================

    private Border? _dropRow;

    /// <summary>The task a drop at this point is for: a row under it, the open panel, or none for a new task.</summary>
    private string? DropTarget(DragEventArgs e, out Border? row)
    {
        row = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is Border { Tag: string id } border && _store.Find(id) is not null
                && (IsInside(border, Groups) || IsInside(border, ClosedList)))
            {
                row = border;
                return id;
            }

            if (node == Detail) return _selected;
        }

        return null;
    }

    /// <summary>Plain text dropped into a text box is that box's business; an email is ours.</summary>
    private static bool LeaveToTextBox(DragEventArgs e) =>
        e.OriginalSource is DependencyObject source && FindTextBox(source) is not null
        && !e.Data.GetDataPresent("FileGroupDescriptorW") && !e.Data.GetDataPresent(DataFormats.FileDrop);

    private static TextBox? FindTextBox(DependencyObject source)
    {
        for (var node = source; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBox box) return box;
        }
        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
       
        if (LeaveToTextBox(e)) return;
        if (!EmailDrop.CanTake(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        var target = DropTarget(e, out var row);
        if (row != _dropRow)
        {
            ClearDropRow();
            _dropRow = row;
            if (row is not null) row.Background = Brush("AccentSoft");
        }

        EmailZone.Ready(target is not null && _store.Find(target) is { } task
            ? $"Drop to add this email to “{task.Title}”"
            : "Drop to make a task from this email");
    }

    /// <remarks>
    /// WPF raises a leave each time the pointer crosses from one child to another, so a leave
    /// still over the page ends the drag only if no drag-over follows — otherwise the drop box
    /// flickers off, and a drag cancelled with Esc would leave it lit.
    /// </remarks>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement page && DropZone.StillOver(page, e)) EmailZone.MaybeLeft(ClearDropRow);
        else EndDrag();
    }

    private void EndDrag()
    {
        ClearDropRow();
        EmailZone.Rest();
    }

    private void ClearDropRow()
    {
        if (_dropRow is null) return;
        _dropRow.Background = _dropRow.Tag as string == _selected ? Brush("Selected") : Brushes.Transparent;
        _dropRow = null;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (LeaveToTextBox(e)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;

        var target = DropTarget(e, out _);
        EndDrag();

        EmailZone.Reading();
        var emails = await EmailDrop.ReadAsync(e.Data);
        EmailZone.Rest();
        if (emails.Count == 0)
        {
            _undo = null;
            UndoText.Text = "That couldn’t be read. Copy the email’s text and paste it into a task instead.";
            UndoBar.Visibility = Visibility.Visible;
            return;
        }

        var now = DateTimeOffset.Now;

        // Onto a task: each email is attached to it, beside its notes rather than in them.
        if (target is not null && _store.Find(target) is { } task)
        {
            foreach (var email in emails)
            {
                _store.AddEmail(task.Id, email.ToTaskEmail(now));
                _store.AddNote(task.Id, $"Email attached: {email.Subject}{From(email)}", TaskNote.App);
            }

            _selected = task.Id;
            _emailOpen = true;
            Refresh();
            return;
        }

        // Anywhere else: a task per email, with the email attached.
        string? first = null;
        foreach (var email in emails)
        {
            var made = _store.Add(email.TaskTitle, _category);
            _store.AddEmail(made.Id, email.ToTaskEmail(now));
            _store.AddNote(made.Id, $"Created from an email{From(email)}", TaskNote.App);
            first ??= made.Id;
        }

        _selected = first;
        _emailOpen = true;
        Refresh();
    }

    private static string From(DroppedEmail email) => email.From is { Length: > 0 } from ? $" from {from}" : string.Empty;

    // ============================== helpers ==============================

    /// <summary>"today", "tomorrow", "yesterday", or "Fri 25 Sep".</summary>
    internal static string Day(DateOnly day)
    {
        var gap = day.DayNumber - Today.DayNumber;
        return gap switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            _ => day.Year == Today.Year ? day.ToString("ddd d MMM", Display) : day.ToString("ddd d MMM yyyy", Display),
        };
    }

    internal static string Clock(TimeOnly time) =>
        time.ToString(time.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var node = child; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == parent) return true;
        }
        return false;
    }

    private Style Styled(string key) => (Style)FindResource(key);

    private Brush Brush(string key) => (Brush)FindResource(key);
}
