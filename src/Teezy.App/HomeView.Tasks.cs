using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Home ▸ Tasks: today's and late tasks, the week ahead, and a box to add one.</summary>
public partial class HomeView
{
    private const int TodayRows = 8;
    private const int SoonRows = 6;

    private TaskStore? _tasks;
    private Action<string>? _openTask;
    private Action? _openTasks;

    /// <summary>Hooked up by the window, which has the store.</summary>
    /// <summary>Matches a typed #category to the list in Settings, adding it if new. Set by the window.</summary>
    private Func<string?, string?>? _category;

    internal void AttachTasks(TaskStore? tasks, Action<string> openTask, Action openTasks, Func<string?, string?>? category = null)
    {
        if (_tasks is not null || tasks is null) return;
        _tasks = tasks;
        _category = category;
        _openTask = openTask;
        _openTasks = openTasks;
        _tasks.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) ShowTasksCard(); });
        ShowTasksCard();
    }

    private void ShowTasksCard()
    {
        if (_tasks is null) return;
        TasksCard.Visibility = Visibility.Visible;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var groups = TaskPlan.Arrange(_tasks.Visible, today);
        List<TaskItem> Bucket(TaskBucket b) => [.. groups.Where(g => g.Bucket == b).SelectMany(g => g.Tasks)];

        var late = Bucket(TaskBucket.Overdue);
        var due = Bucket(TaskBucket.Today);
        var soon = Bucket(TaskBucket.Upcoming).Where(t => t.Due!.Value.DayNumber - today.DayNumber <= 7).ToList();
        var open = groups.Sum(g => g.Tasks.Count);

        TasksCount.Text = open == 0 ? string.Empty : $"{open} open";
        TasksTodayLabel.Text = late.Count > 0 ? $"TODAY · {late.Count} LATE" : "TODAY";
        TasksTodayLabel.Foreground = late.Count > 0 ? Brand.Brush("CautionBorder") : Brand.Muted;

        Fill(TasksToday, [.. late, .. due], TodayRows, today,
            open == 0 ? "Nothing on the list. Add one above." : "Nothing due today.");
        Fill(TasksSoon, soon, SoonRows, today, "Nothing in the next week.");
    }

    private void Fill(StackPanel panel, IReadOnlyList<TaskItem> tasks, int max, DateOnly today, string empty)
    {
        panel.Children.Clear();
        if (tasks.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = empty, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        foreach (var task in tasks.Take(max)) panel.Children.Add(TaskRow(task, today));

        if (tasks.Count > max)
        {
            var more = new Button
            {
                Content = $"{tasks.Count - max} more",
                Style = (Style)FindResource("Quiet"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            more.Click += (_, _) => _openTasks?.Invoke();
            panel.Children.Add(more);
        }
    }

    private FrameworkElement TaskRow(TaskItem task, DateOnly today)
    {
        var tick = new CheckBox
        {
            Style = (Style)FindResource("Tick"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Close",
        };
        tick.Checked += (_, _) => _tasks?.Close(task.Id);

        var title = new TextBlock
        {
            Text = task.Title,
            Foreground = Brand.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var late = task.Due < today;
        var when = new TextBlock
        {
            Text = When(task, today),
            FontSize = 12,
            Foreground = late ? Brand.Brush("CautionBorder") : Brand.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(when, FontNumeralAlignment.Tabular);

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        line.Children.Add(tick);
        line.Children.Add(title);

        if (task.Category is { } category)
        {
            var tag = new Border
            {
                Style = (Style)FindResource("Tag"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = category, Style = (Style)FindResource("TagText") },
            };
            Grid.SetColumn(tag, 2);
            line.Children.Add(tag);
        }

        Grid.SetColumn(when, 3);
        line.Children.Add(when);

        var row = new Border
        {
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(-6, 0, -6, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = line,
        };
        row.MouseEnter += (_, _) => row.Background = Brand.Brush("Raised");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Within(source, tick)) return;
            _openTask?.Invoke(task.Id);
        };
        return row;
    }

    private static string When(TaskItem task, DateOnly today)
    {
        if (task.Due is not { } due) return string.Empty;
        var days = today.DayNumber - due.DayNumber;
        if (days > 0) return days == 1 ? "yesterday" : $"{days} days late";
        var day = due == today ? string.Empty : TasksView.Day(due);
        return task.DueTime is { } time ? $"{day} {TasksView.Clock(time)}".Trim() : day;
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

    private void OnOpenTasks(object sender, RoutedEventArgs e) => _openTasks?.Invoke();

    private void OnTaskDragOver(object sender, DragEventArgs e)
    {
        // Plain text into the add box is the box's; an email from Outlook is a task.
        if (e.OriginalSource is DependencyObject over && Within(over, TaskAddBox) && !e.Data.GetDataPresent("FileGroupDescriptorW")
            && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Effects = _tasks is not null && EmailDrop.CanTake(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>An email dropped on the card: a task for today, with the email kept as its first note.</summary>
    private void OnTaskDrop(object sender, DragEventArgs e)
    {
        if (_tasks is null) return;
        if (e.OriginalSource is DependencyObject over && Within(over, TaskAddBox) && !e.Data.GetDataPresent("FileGroupDescriptorW")
            && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var email in EmailDrop.Read(e.Data))
        {
            var made = _tasks.Add(email.TaskTitle, due: today);
            _tasks.AddEmail(made.Id, email.ToTaskEmail(DateTimeOffset.Now));
            _tasks.AddNote(made.Id, email.From is { Length: > 0 } from ? $"Created from an email from {from}" : "Created from an email", TaskNote.App);
        }
    }

    private void OnTaskAddTyped(object sender, TextChangedEventArgs e) =>
        TaskAddPlaceholder.Visibility = TaskAddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnTaskAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { TaskAddBox.Clear(); return; }
        if (e.Key != Key.Enter || _tasks is null) return;
        e.Handled = true;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var parsed = TaskInput.Parse(TaskAddBox.Text, today);
        if (parsed.Title.Length == 0) return;

        // Added from Home with no day, it is for today — that is what this card shows.
        var due = parsed.Due ?? today;
        _tasks.Add(parsed.Title, _category?.Invoke(parsed.Category), due: due, dueTime: parsed.DueTime,
            remind: parsed.DueTime is { } time ? TaskPlan.At(due, time) : null);
        TaskAddBox.Clear();
    }
}
