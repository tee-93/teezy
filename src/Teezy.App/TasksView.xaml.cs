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
    private string? _selected;
    private string? _category;
    private bool _showClosed;

    // What Undo puts back: the task closed, and the follow-up made with it, if any.
    private (string Closed, string? FollowUp)? _undo;

    internal TasksView(TaskStore store, IMailAdvisor? advisor)
    {
        InitializeComponent();
        _store = store;
        _advisor = advisor;
        EmailSection.Visibility = advisor is not null ? Visibility.Visible : Visibility.Collapsed;
        SteerBox.TextChanged += (_, _) =>
            SteerPlaceholder.Visibility = SteerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Changes arrive from sync and reminders on other threads.
        _store.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Refresh(); });
        Loaded += (_, _) => Refresh();
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

        if (_category is not null && !TaskPlan.Categories(all).Contains(_category, StringComparer.OrdinalIgnoreCase))
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

        // A time with no day means today.
        var due = parsed.Due ?? (parsed.DueTime is not null ? Today : null);
        _store.Add(parsed.Title, parsed.Category ?? _category, due: due, dueTime: parsed.DueTime);
        AddBox.Clear();
    }

    // ============================== the list ==============================

    private void BuildFilters(IReadOnlyList<TaskItem> all)
    {
        Filters.Children.Clear();
        var categories = TaskPlan.Categories(all);
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
        FollowUpBox.Clear();
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

    private void OnFollowUpOn(object sender, RoutedEventArgs e) => FollowUpFromBox();

    private void OnFollowUpKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FollowUpFromBox();
    }

    private void FollowUpFromBox()
    {
        if (TaskInput.TryDate(FollowUpBox.Text, Today, out var day))
        {
            DateProblem.Visibility = Visibility.Collapsed;
            FollowUp(day);
        }
        else
        {
            ShowProblem("Try a day like fri, next week, in 10 days or 3/10.");
        }
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

    // ============================== the panel ==============================

    private void OnDeselect(object sender, RoutedEventArgs e)
    {
        _selected = null;
        Refresh();
    }

    private bool DetailHasFocus() =>
        Detail.Visibility == Visibility.Visible && Detail.IsKeyboardFocusWithin;

    /// <param name="fields">Whether to refill the text boxes, which is skipped while one is being typed in.</param>
    private void ShowDetail(bool fields)
    {
        var task = _selected is { } id ? _store.Find(id) : null;
        if (task is null)
        {
            _selected = null;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        var shown = Detail.Tag as string;
        var changed = shown != task.Id;
        Detail.Tag = task.Id;
        Detail.Visibility = Visibility.Visible;

        if (changed)
        {
            EmailBox.Clear();
            SteerBox.Clear();
            NoteBox.Clear();
            FollowUpBox.Clear();
            AdvicePanel.Children.Clear();
            AdvicePanel.Visibility = Visibility.Collapsed;
            DateProblem.Visibility = Visibility.Collapsed;
            DeleteButton.Tag = null;
            DeleteButton.Content = "Delete task";
        }

        if (fields || changed)
        {
            TitleBox.Text = task.Title;
            DueBox.Text = BoxDate(task.Due);
            TimeBox.Text = task.DueTime is { } t ? Clock(t) : string.Empty;
            StartBox.Text = BoxDate(task.Start);
            CategoryBox.Text = task.Category ?? string.Empty;
        }

        DetailState.Text = task.IsOpen
            ? BucketName(TaskPlan.BucketOf(task, Today))
            : $"CLOSED {Day(DateOnly.FromDateTime(task.Closed!.Value.LocalDateTime)).ToUpper(Display)}";
        DetailState.Foreground = task.IsOpen && TaskPlan.BucketOf(task, Today) == TaskBucket.Overdue
            ? Brush("CautionBorder")
            : Brush("Muted");

        OpenActions.Visibility = task.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ReopenButton.Visibility = task.IsOpen ? Visibility.Collapsed : Visibility.Visible;

        BuildFollowUpChoices();
        BuildCategoryChips(task);
        BuildNotes(task);
        BuildChain(task);
    }

    private void BuildCategoryChips(TaskItem task)
    {
        CategoryChips.Children.Clear();
        foreach (var category in TaskPlan.Categories(_store.Visible))
        {
            if (string.Equals(category, task.Category, StringComparison.OrdinalIgnoreCase)) continue;
            var chip = new Button { Content = category, Style = Styled("Quiet"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
            chip.Click += (_, _) =>
            {
                if (_store.Find(task.Id) is { } current) _store.Update(current with { Category = category });
            };
            CategoryChips.Children.Add(chip);
        }
    }

    private void BuildNotes(TaskItem task)
    {
        NotesList.Children.Clear();
        if (task.Notes.Count == 0)
        {
            NotesList.Children.Add(new TextBlock { Text = "No notes yet.", Style = Styled("FormHint"), Margin = new Thickness(0, 0, 0, 8) });
            return;
        }

        foreach (var note in task.Notes)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            block.Children.Add(new TextBlock
            {
                Text = note.At.LocalDateTime.ToString("ddd d MMM, h:mm tt", Display),
                Style = Styled("FormHint"),
                Margin = new Thickness(0),
            });

            // A read-only box so a note can be selected and copied.
            block.Children.Add(new TextBox
            {
                Text = note.Text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush("Body"),
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 13,
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 2, 0, 0),
            });
            NotesList.Children.Add(block);
        }
    }

    private void BuildChain(TaskItem task)
    {
        ChainList.Children.Clear();
        var chain = TaskPlan.Chain(_store.Visible, task.Id);
        ChainSection.Visibility = chain.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (chain.Count < 2) return;

        for (var i = 0; i < chain.Count; i++)
        {
            var link = chain[i];
            var when = link.Closed is { } closed
                ? $"closed {Day(DateOnly.FromDateTime(closed.LocalDateTime))}"
                : link.Due is { } due ? $"open · due {Day(due)}" : "open";

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = link.Title,
                Foreground = link.Id == task.Id ? Brush("AccentInk") : link.IsOpen ? Brush("Ink") : Brush("Muted"),
                FontWeight = link.Id == task.Id ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock { Text = when, FontSize = 12, Foreground = Brush("Muted") });

            var row = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                BorderBrush = Brush("Hairline"),
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Background = Brushes.Transparent,
                Cursor = link.Id == task.Id ? Cursors.Arrow : Cursors.Hand,
                Child = text,
            };
            if (link.Id != task.Id)
            {
                row.MouseEnter += (_, _) => row.Background = Brush("Raised");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => Select(link.Id);
            }

            ChainList.Children.Add(row);
        }
    }

    // ---- editing ----

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ShowDetail(fields: true);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (sender == TitleBox) CommitTitle();
        else CommitDates();
    }

    private void OnTitleCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitTitle();

    private void OnDatesCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitDates();

    private void CommitTitle()
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { TitleBox.Text = task.Title; return; }
        if (title != task.Title) _store.Update(task with { Title = title });
    }

    private void CommitDates()
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        var today = Today;

        if (!ReadDate(DueBox, task.Due, today, out var due))
        {
            ShowProblem("That due date wasn’t understood. Try fri, tomorrow, in 3 days or 25/9.");
            return;
        }

        if (!ReadDate(StartBox, task.Start, today, out var start))
        {
            ShowProblem("That start date wasn’t understood. Try mon, next week or 1/10.");
            return;
        }

        TimeOnly? time = null;
        var timeText = TimeBox.Text.Trim();
        if (timeText.Length > 0)
        {
            if (!TaskInput.TryTime(timeText, out var parsed))
            {
                ShowProblem("That time wasn’t understood. Try 2pm, 9:30am or 14:30.");
                return;
            }
            time = parsed;
        }

        // A reminder needs a day; a time alone means today.
        if (time is not null && due is null) due = today;

        DateProblem.Visibility = Visibility.Collapsed;
        var category = CategoryBox.Text.Trim().TrimStart('#');
        var edited = task with
        {
            Due = due,
            DueTime = time,
            Start = start,
            Category = category.Length == 0 ? null : category,
        };

        if (edited == task) return;

        // A moved reminder is a new reminder.
        if (edited.Due != task.Due || edited.DueTime != task.DueTime) edited = edited with { Reminded = null };
        _store.Update(edited);
    }

    private static bool ReadDate(TextBox box, DateOnly? current, DateOnly today, out DateOnly? value)
    {
        var text = box.Text.Trim();
        value = current;
        if (text == BoxDate(current)) return true;
        if (text.Length == 0) { value = null; return true; }
        if (!TaskInput.TryDate(text, today, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private void ShowProblem(string text)
    {
        DateProblem.Text = text;
        DateProblem.Visibility = Visibility.Visible;
    }

    // ---- notes ----

    private void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            AddNote();
        }
    }

    private void OnAddNote(object sender, RoutedEventArgs e) => AddNote();

    private void AddNote()
    {
        if (_selected is not { } id || NoteBox.Text.Trim().Length == 0) return;
        _store.AddNote(id, NoteBox.Text);
        NoteBox.Clear();
    }

    // ============================== the AI, on request ==============================

    private void OnNextSteps(object sender, RoutedEventArgs e) => _ = AdviseAsync(AdviceKind.NextSteps);

    private void OnDraftReply(object sender, RoutedEventArgs e) => _ = AdviseAsync(AdviceKind.DraftReply);

    private async Task AdviseAsync(AdviceKind kind)
    {
        if (_advisor is null || _selected is not { } id) return;

        var email = EmailBox.Text.Trim();
        AdvicePanel.Visibility = Visibility.Visible;
        AdvicePanel.Children.Clear();

        if (email.Length == 0)
        {
            AdvicePanel.Children.Add(new TextBlock { Text = "Paste the email into the box first.", Style = Styled("FormHint") });
            return;
        }

        AdvicePanel.Children.Add(new TextBlock
        {
            Text = kind == AdviceKind.DraftReply ? "Drafting a reply…" : "Thinking about next steps…",
            Style = Styled("FormHint"),
        });
        NextStepsButton.IsEnabled = DraftReplyButton.IsEnabled = false;

        string? result;
        try
        {
            result = await _advisor.AdviseAsync(kind, _store.Find(id), email, SteerBox.Text, DateTimeOffset.Now);
        }
        catch (AssistantUnavailableException problem)
        {
            AdvicePanel.Children.Clear();
            AdvicePanel.Children.Add(new TextBlock { Text = problem.Message, Style = Styled("FormHint") });
            return;
        }
        finally
        {
            NextStepsButton.IsEnabled = DraftReplyButton.IsEnabled = true;
        }

        // The panel may have moved on to another task while Claude was answering.
        if (_selected != id) return;
        AdvicePanel.Children.Clear();

        var output = new TextBox
        {
            Text = result ?? "Nothing useful came back. Try again.",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brush("Sunken"),
            Foreground = Brush("Body"),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 13,
        };
        AdvicePanel.Children.Add(output);
        if (result is null) return;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var copy = new Button { Content = "Copy", Style = Styled("Primary") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(output.Text); copy.Content = "Copied"; }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = "Clipboard busy — try again"; }
        };
        buttons.Children.Add(copy);

        var keep = new Button { Content = "Save to notes", Style = Styled("Secondary"), Margin = new Thickness(8, 0, 0, 0) };
        keep.Click += (_, _) =>
        {
            _store.AddNote(id, (kind == AdviceKind.DraftReply ? "Draft reply:\n" : "Next steps:\n") + output.Text);
            keep.Content = "Saved";
            keep.IsEnabled = false;
        };
        buttons.Children.Add(keep);

        AdvicePanel.Children.Add(buttons);
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

        AddPreview.Text = target is not null && _store.Find(target) is { } task
            ? $"Drop to add this email to “{task.Title}”"
            : "Drop to make a task from this email";
        AddPreview.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => EndDrag();

    private void EndDrag()
    {
        ClearDropRow();
        AddPreview.Visibility = AddBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AddBox.Text.Trim().Length > 0) OnAddTyped(AddBox, null!);
    }

    private void ClearDropRow()
    {
        if (_dropRow is null) return;
        _dropRow.Background = _dropRow.Tag as string == _selected ? Brush("Selected") : Brushes.Transparent;
        _dropRow = null;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (LeaveToTextBox(e)) return;
        e.Handled = true;

        var target = DropTarget(e, out _);
        EndDrag();

        var emails = EmailDrop.Read(e.Data);
        if (emails.Count == 0)
        {
            _undo = null;
            UndoText.Text = "That couldn’t be read. Copy the email’s text and paste it into a task instead.";
            UndoBar.Visibility = Visibility.Visible;
            return;
        }

        // Onto a task: each email becomes a note on it.
        if (target is not null && _store.Find(target) is { } task)
        {
            foreach (var email in emails) _store.AddNote(task.Id, email.ToNote());
            _selected = task.Id;
            Refresh();
            EmailBox.Text = emails[^1].ToNote();
            return;
        }

        // Anywhere else: a task per email, with the email kept as its first note.
        string? first = null;
        foreach (var email in emails)
        {
            var made = _store.Add(email.TaskTitle, _category);
            _store.AddNote(made.Id, email.ToNote());
            first ??= made.Id;
        }

        _selected = first;
        Refresh();
        EmailBox.Text = emails[0].ToNote();
    }

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

    /// <summary>What a date box shows: a date the reader reads back.</summary>
    private static string BoxDate(DateOnly? day) =>
        day is not { } d ? string.Empty : d.ToString(d.Year == Today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);

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
