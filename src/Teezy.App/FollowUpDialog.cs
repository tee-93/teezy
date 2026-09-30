using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>What closing a follow-up task turned into.</summary>
public enum FollowUpOutcome
{
    /// <summary>Dismissed without deciding — the task is left exactly as it was, still open.</summary>
    Cancelled,

    /// <summary>Closed, with nothing booked after it.</summary>
    JustClose,

    /// <summary>Closed, and a new follow-up booked in its place.</summary>
    CreateFollowUp,
}

/// <summary>What came of showing the dialog: whether the task closed, and what followed it.</summary>
public readonly record struct FollowUpResult(bool Closed, string? FollowUpId);

/// <summary>
/// Asks what happens next when a quote follow-up is ticked off — a kind and a date for the next
/// one, or just close it — rather than booking another one silently. This is what replaced the
/// old fixed cadence: see <see cref="Teezy.Core.Quotes.QuoteChasing"/>.
/// </summary>
public sealed class FollowUpDialog : Window
{
    private readonly ComboBox _kind;
    private readonly TextBox _title;
    private readonly DateTimeField _day;
    private readonly Button _create;
    private string _autoTitle = "";

    public FollowUpOutcome Outcome { get; private set; } = FollowUpOutcome.Cancelled;
    public string? ChosenKind { get; private set; }
    public string ChosenTitle { get; private set; } = "";
    public DateOnly? Due { get; private set; }

    public FollowUpDialog(Window owner, TeezySettings settings, TaskItem task, string? customer)
    {
        Owner = owner;
        Title = "TeezyFlow";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brand.Brush("Paper");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        var kinds = settings.FollowUpKinds.Count > 0 ? settings.FollowUpKinds : ["Follow-up"];

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(new TextBlock { Text = "Follow up again?", Style = (Style)FindResource("H2") });
        panel.Children.Add(new TextBlock
        {
            Text = customer is { Length: > 0 } ? $"On {customer} — “{task.Title}” is done." : $"“{task.Title}” is done.",
            Foreground = Brand.Brush("Body"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16),
        });

        panel.Children.Add(new TextBlock { Text = "KIND", Style = (Style)FindResource("SectionLabel") });
        _kind = new ComboBox { ItemsSource = kinds, Margin = new Thickness(0, 0, 0, 12) };
        _kind.SelectedItem = kinds.FirstOrDefault(k => string.Equals(k, task.Kind, StringComparison.OrdinalIgnoreCase))
            ?? kinds[0];
        panel.Children.Add(_kind);

        panel.Children.Add(new TextBlock { Text = "TITLE", Style = (Style)FindResource("SectionLabel") });
        _title = new TextBox { Style = (Style)FindResource("Field"), Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(_title);

        panel.Children.Add(new TextBlock { Text = "WHEN", Style = (Style)FindResource("SectionLabel") });
        var choices = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var today = DateOnly.FromDateTime(DateTime.Today);
        (string Label, DateOnly Day)[] quick =
        [
            ("Tomorrow", TaskPlan.Workday(today.AddDays(1))),
            ("In 3 days", TaskPlan.Workday(today.AddDays(3))),
            ("In a week", TaskPlan.Workday(today.AddDays(7))),
            ("In 2 weeks", TaskPlan.Workday(today.AddDays(14))),
        ];
        foreach (var (label, day) in quick)
        {
            var button = new Button
            {
                Content = $"{label} · {TasksView.Day(day)}",
                Style = (Style)FindResource("Secondary"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) => SetDay(day);
            choices.Children.Add(button);
        }
        panel.Children.Add(choices);

        _day = new DateTimeField { Placeholder = "Another day…", ShowTime = false, Margin = new Thickness(0, 0, 0, 16) };
        _day.Changed += () => _create.IsEnabled = _day.Date is not null;
        panel.Children.Add(_day);

        var justClose = new Button { Content = "Just close", Style = (Style)FindResource("Secondary") };
        justClose.Click += (_, _) => { Outcome = FollowUpOutcome.JustClose; DialogResult = true; };

        _create = new Button
        {
            Content = "Create follow-up",
            Style = (Style)FindResource("Primary"),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            IsEnabled = false,
        };
        _create.Click += (_, _) =>
        {
            if (_day.Date is not { } due) return;
            ChosenKind = _kind.SelectedItem as string ?? kinds[0];
            ChosenTitle = _title.Text.Trim();
            Due = due;
            Outcome = FollowUpOutcome.CreateFollowUp;
            DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        buttons.Children.Add(justClose);
        buttons.Children.Add(_create);
        panel.Children.Add(buttons);

        Content = panel;

        // The title tracks the kind until someone types their own; picking a different kind
        // after that leaves what they wrote alone.
        _kind.SelectionChanged += (_, _) =>
        {
            var next = DefaultTitle(_kind.SelectedItem as string ?? kinds[0], customer);
            if (string.Equals(_title.Text, _autoTitle, StringComparison.Ordinal)) _title.Text = next;
            _autoTitle = next;
        };
        _autoTitle = DefaultTitle(_kind.SelectedItem as string ?? kinds[0], customer);
        _title.Text = _autoTitle;

        Loaded += (_, _) => _title.Focus();
    }

    private void SetDay(DateOnly day)
    {
        _day.Set(day, null);
        _create.IsEnabled = true;
    }

    private static string DefaultTitle(string kind, string? customer) =>
        customer is { Length: > 0 } ? $"{kind} — {customer}" : kind;

    /// <summary>
    /// Closes a task, asking first if it is a follow-up (has a <see cref="TaskItem.Kind"/>) what
    /// comes next. An ordinary task — no kind — just closes, no dialog: only a follow-up asks.
    /// </summary>
    public static FollowUpResult CloseWithPrompt(
        Window owner, TaskStore tasks, TeezySettings settings, TaskItem task, string? customer = null)
    {
        if (task.Kind is not { Length: > 0 })
        {
            tasks.Close(task.Id);
            return new FollowUpResult(true, null);
        }

        var dialog = new FollowUpDialog(owner, settings, task, customer);
        if (dialog.ShowDialog() != true || dialog.Outcome == FollowUpOutcome.Cancelled)
        {
            return new FollowUpResult(false, null);
        }

        if (dialog.Outcome == FollowUpOutcome.JustClose)
        {
            tasks.Close(task.Id);
            return new FollowUpResult(true, null);
        }

        var next = tasks.CloseAndFollowUp(task.Id, dialog.Due!.Value, dialog.ChosenTitle, kind: dialog.ChosenKind);
        return new FollowUpResult(true, next.Id);
    }
}
