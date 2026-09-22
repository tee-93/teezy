using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Due tasks, in a card above the tray, each with Done, Snooze and Open.</summary>
/// <remarks>
/// Never activated, for the reason the dictation pill is not: it appears while something else is
/// being typed, and taking focus would send the rest of that typing nowhere. See
/// <see cref="HudWindow"/> for why both <c>ShowActivated</c> and <c>WS_EX_NOACTIVATE</c> are needed.
/// </remarks>
public partial class ReminderWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly TaskStore _store;
    private readonly Action<string> _open;
    private readonly List<string> _shown = [];

    /// <param name="open">Opens TeezyFlow at a task.</param>
    public ReminderWindow(TaskStore store, Action<string> open)
    {
        InitializeComponent();
        _store = store;
        _open = open;
        SizeChanged += (_, _) => Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtrW(handle, GWL_EXSTYLE);
        Native.SetWindowLongPtrW(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Adds tasks to the card, showing it if it is not already up.</summary>
    public void Remind(IEnumerable<TaskItem> tasks)
    {
        foreach (var task in tasks)
        {
            if (!_shown.Contains(task.Id)) _shown.Add(task.Id);
        }

        Build();
        if (!IsVisible) Show();
        Place();
    }

    private void Build()
    {
        Items.Children.Clear();
        var tasks = _shown.Select(_store.Find).Where(t => t is { IsOpen: true }).Cast<TaskItem>().ToList();

        if (tasks.Count == 0)
        {
            _shown.Clear();
            Hide();
            return;
        }

        Heading.Text = tasks.Count == 1 ? "TASK DUE" : $"{tasks.Count} TASKS DUE";
        for (var i = 0; i < tasks.Count; i++) Items.Children.Add(Item(tasks[i], first: i == 0));
    }

    private FrameworkElement Item(TaskItem task, bool first)
    {
        var panel = new StackPanel { Margin = new Thickness(0, first ? 4 : 12, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = task.Title,
            Foreground = Brush("Ink"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var when = task.DueTime is { } time ? $"{TasksView.Day(task.Due ?? DateOnly.FromDateTime(DateTime.Today))} {TasksView.Clock(time)}" : "today";
        var meta = task.Category is { } category ? $"{category} · due {when}" : $"Due {when}";
        if (task.Notes.Count > 0) meta += $" · {task.Notes[^1].Text.Split('\n')[0]}";
        panel.Children.Add(new TextBlock
        {
            Text = meta,
            Foreground = Brush("Muted"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 8),
        });

        var buttons = new WrapPanel();
        buttons.Children.Add(Button("Done", "Primary", () => _store.Close(task.Id)));
        buttons.Children.Add(Button("In 1 hour", "Secondary", () => Snooze(task, TimeSpan.FromHours(1))));
        buttons.Children.Add(Button("Tomorrow", "Secondary", () => Tomorrow(task)));
        buttons.Children.Add(Button("Open", "Quiet", () => { Forget(task.Id); _open(task.Id); }));
        panel.Children.Add(buttons);

        return panel;
    }

    private Button Button(string label, string style, Action run)
    {
        var button = new Button { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => { run(); Build(); };
        return button;
    }

    private void Snooze(TaskItem task, TimeSpan by)
    {
        var at = DateTime.Now + by;
        _store.Update(task with
        {
            Due = DateOnly.FromDateTime(at),
            DueTime = new TimeOnly(at.Hour, at.Minute),
            Reminded = null,
        });
        Forget(task.Id);
    }

    private void Tomorrow(TaskItem task)
    {
        _store.Update(task with
        {
            Due = TaskPlan.Workday(DateOnly.FromDateTime(DateTime.Today).AddDays(1)),
            Reminded = null,
        });
        Forget(task.Id);
    }

    private void Forget(string id) => _shown.Remove(id);

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        _shown.Clear();
        Hide();
    }

    /// <summary>Bottom right, above the tray, on the main screen.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
