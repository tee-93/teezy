using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core.Commands;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Flagged emails from classic Outlook, as a task list.</summary>
/// <remarks>
/// <para>
/// The flag is the tick box. Ticking a task completes its flag in Outlook, the row goes, and an
/// Undo puts the flag back — so this list and Outlook's own To-Do list are the same list.
/// </para>
/// <para>
/// <b>The AI sees an email only when a button on that email is pressed</b>, and what comes back
/// is text on this page: next steps to read, or a reply to copy into Outlook. Nothing is saved to
/// Outlook or sent. See <see cref="IMailAdvisor"/> for why the request carries no tools.
/// </para>
/// </remarks>
public partial class TasksView : UserControl
{
    private readonly IMailTasks _tasks;
    private readonly IMailAdvisor? _advisor;
    private MailTask? _lastDone;
    private bool _loading;

    internal TasksView(IMailTasks tasks, IMailAdvisor? advisor)
    {
        InitializeComponent();
        _tasks = tasks;
        _advisor = advisor;
        AiNote.Visibility = advisor is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    public async void Refresh() => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;

        try
        {
            if (!_tasks.IsAvailable)
            {
                ShowEmpty("Open classic Outlook",
                    "Your flagged emails appear here while classic Outlook is running on this computer. It can stay minimised.");
                return;
            }

            var open = await _tasks.OpenAsync();
            if (open.Count == 0)
            {
                ShowEmpty("Nothing flagged", "Flag an email in Outlook and it appears here as a task.");
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            Lede.Text = $"{open.Count} flagged in Outlook, by category. Tick one off here and it’s done in Outlook too.";
            Build(TaskList.Arrange(open));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            ShowEmpty("Outlook didn’t answer", e.Message);
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowEmpty(string title, string text)
    {
        Groups.Children.Clear();
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
    }

    // ---- building the list ----

    private void Build(IReadOnlyList<TaskGroup> groups)
    {
        Groups.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);

        foreach (var group in groups)
        {
            Groups.Children.Add(new TextBlock
            {
                Text = group.Name.ToUpper(CultureInfo.CurrentCulture),
                Style = Styled("SectionLabel"),
                Margin = new Thickness(0, Groups.Children.Count == 0 ? 0 : 20, 0, 6),
            });

            var rows = new StackPanel();
            for (var i = 0; i < group.Tasks.Count; i++)
            {
                rows.Children.Add(Row(group.Tasks[i], first: i == 0, today));
            }

            Groups.Children.Add(new Border { Style = (Style)FindResource("ListCard"), Child = rows });
        }
    }

    private FrameworkElement Row(MailTask task, bool first, DateOnly today)
    {
        var tick = new CheckBox { Style = (Style)FindResource("Tick"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };

        var subject = new TextBlock { Text = task.Subject, Foreground = Brush("Ink"), TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = new TextBlock
        {
            Text = Meta(task, today),
            FontSize = 12,
            Foreground = task.IsOverdue(today) ? Brush("CautionBorder") : Brush("Muted"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(meta, FontNumeralAlignment.Tabular);

        var text = new StackPanel();
        text.Children.Add(subject);
        text.Children.Add(meta);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        actions.Children.Add(Action("Open in Outlook", () => _ = _tasks.OpenInOutlookAsync(task.Id)));

        var advice = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(28, 10, 0, 0) };
        if (task.IsEmail && _advisor is not null)
        {
            actions.Children.Add(Action("Next steps", () => _ = AdviseAsync(task, AdviceKind.NextSteps, advice, null)));
            actions.Children.Add(Action("Draft reply", () => _ = AdviseAsync(task, AdviceKind.DraftReply, advice, null)));
        }

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(actions, 2);
        line.Children.Add(tick);
        line.Children.Add(text);
        line.Children.Add(actions);

        var body = new StackPanel();
        body.Children.Add(line);
        body.Children.Add(advice);

        var row = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = Brushes.Transparent,
            Child = body,
        };

        // Actions show on hover, as on Transcripts, so the list reads as a list.
        actions.Opacity = 0.0;
        row.MouseEnter += (_, _) => { row.Background = Brush("Raised"); actions.Opacity = 1; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; actions.Opacity = 0; };

        tick.Checked += async (_, _) => await CompleteAsync(task, row);
        return row;
    }

    private static string Meta(MailTask task, DateOnly today)
    {
        var parts = new List<string>();
        if (task.From.Length > 0) parts.Add(task.From);
        parts.Add(Ago(task.Received));
        if (task.Due is { } due)
        {
            parts.Add(due < today ? $"overdue since {due:ddd d MMM}"
                : due == today ? "due today"
                : $"due {due:ddd d MMM}");
        }
        if (task.Categories.Count > 1) parts.Add(string.Join(", ", task.Categories.Skip(1)));
        return string.Join(" · ", parts);
    }

    private static string Ago(DateTimeOffset when)
    {
        var days = (DateTime.Today - when.LocalDateTime.Date).Days;
        return days switch
        {
            0 => $"today {when.LocalDateTime:h:mm tt}",
            1 => "yesterday",
            < 7 => $"{when.LocalDateTime:dddd}",
            _ => $"{when.LocalDateTime:d MMM}",
        };
    }

    // ---- ticking ----

    private async Task CompleteAsync(MailTask task, Border row)
    {
        row.IsEnabled = false;
        try
        {
            await _tasks.CompleteAsync(task.Id);
            _lastDone = task;
            UndoText.Text = $"Done: {task.Subject}";
            UndoBar.Visibility = Visibility.Visible;
            await LoadAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            row.IsEnabled = true;
            UndoText.Text = $"Outlook didn’t accept that: {e.Message}";
            UndoBar.Visibility = Visibility.Visible;
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        UndoBar.Visibility = Visibility.Collapsed;
        if (_lastDone is not { } task) return;
        _lastDone = null;

        try { await _tasks.ReopenAsync(task.Id); }
        catch (Exception problem) when (problem is InvalidOperationException or System.Runtime.InteropServices.COMException
                                            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            UndoText.Text = $"Couldn’t put the flag back: {problem.Message}";
            UndoBar.Visibility = Visibility.Visible;
        }

        await LoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        UndoBar.Visibility = Visibility.Collapsed;
        await LoadAsync();
    }

    // ---- the AI, on request ----

    private async Task AdviseAsync(MailTask task, AdviceKind kind, StackPanel panel, string? instruction)
    {
        if (_advisor is null) return;

        panel.Visibility = Visibility.Visible;
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = kind == AdviceKind.DraftReply ? "Drafting a reply…" : "Thinking about next steps…",
            Style = Styled("Hint"),
        });

        string? result;
        try
        {
            var body = await _tasks.BodyAsync(task.Id) ?? string.Empty;
            result = await _advisor.AdviseAsync(kind, task, body, instruction, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is AssistantUnavailableException or InvalidOperationException
                                      or System.Runtime.InteropServices.COMException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            result = null;
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = e.Message, Style = Styled("Hint") });
            return;
        }

        panel.Children.Clear();

        // A read-only text box rather than a text block, so any part of it can be selected too.
        var output = new TextBox
        {
            Text = result ?? "Nothing useful came back. Try again, or open it in Outlook.",
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
        panel.Children.Add(output);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (result is not null)
        {
            var copy = new Button { Content = "Copy", Style = Styled("Secondary") };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(output.Text); copy.Content = "Copied"; }
                catch (System.Runtime.InteropServices.COMException) { copy.Content = "Clipboard busy — try again"; }
            };
            buttons.Children.Add(copy);
        }

        if (kind == AdviceKind.DraftReply)
        {
            // Redraft with a steer: "say yes but not before Friday".
            var steer = new TextBox { Style = (Style)FindResource("BareText"), MinWidth = 260 };
            var box = new Border { Style = (Style)FindResource("FieldBox"), Child = steer, Margin = new Thickness(8, 0, 0, 0), Width = 300 };
            var again = new Button { Content = "Redraft", Style = Styled("Quiet"), Margin = new Thickness(8, 0, 0, 0) };
            again.Click += (_, _) => _ = AdviseAsync(task, kind, panel, steer.Text);
            steer.ToolTip = "Anything to steer the reply, e.g. “say yes, but not before Friday”";
            buttons.Children.Add(box);
            buttons.Children.Add(again);
        }

        var close = new Button { Content = "Close", Style = Styled("Quiet"), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => { panel.Visibility = Visibility.Collapsed; panel.Children.Clear(); };
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
    }

    // ---- helpers ----

    private Button Action(string label, Action run)
    {
        var button = new Button { Content = label, Style = Styled("Quiet"), Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => run();
        return button;
    }

    private Style Styled(string key) => (Style)FindResource(key);


    private Brush Brush(string key) => (Brush)FindResource(key);
}
