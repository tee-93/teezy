using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Commands;
using Teezy.Core.Home;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>The weekday morning briefing: the day in one window, first thing.</summary>
/// <remarks>
/// <para>
/// Arrives without taking the keyboard (<see cref="Window.ShowActivated"/> false): at 8:30 someone
/// may already be typing, and the rest of that sentence belongs where they were typing it. It
/// stays on top until dealt with, and a click anywhere in it is the usual click.
/// </para>
/// <para>
/// Tasks can be ticked off or opened from here. The AI summary, when switched on, fills in at
/// the top a moment later; the plain list is never kept waiting for it.
/// </para>
/// </remarks>
public sealed class BriefingWindow : Window
{
    private readonly TaskStore _tasks;
    private readonly Action<string> _openTask;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 };

    /// <param name="summarise">Writes the AI summary; null when that is switched off.</param>
    /// <param name="speak">Reads the briefing aloud; null when no voice is available.</param>
    public BriefingWindow(Briefing briefing, TaskStore tasks, Action<string> openTask, Action openApp,
        Func<Task<string?>>? summarise, Action<string>? speak)
    {
        _tasks = tasks;
        _openTask = openTask;

        Title = "TeezyFlow — morning briefing";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = false;
        Topmost = true;
        Background = Brand.Brush("Paper");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        // Only the first moment is on top: once looked at, it behaves like any window.
        Activated += (_, _) => Topmost = false;

        // Centred once its height is known: a window that sizes to its content is placed by
        // WPF before that, and lands in the corner.
        ContentRendered += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 3);
        };

        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var mark = new Image { Source = (ImageSource)FindResource("MarkImage"), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var greeting = new TextBlock { Text = briefing.Greeting, Style = (Style)FindResource("Display"), FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(mark);
        head.Children.Add(greeting);
        page.Children.Add(head);
        page.Children.Add(new TextBlock { Text = briefing.Date, Style = (Style)FindResource("Lede"), Margin = new Thickness(32, 0, 0, 12) });
        page.Children.Add(new TextBlock { Text = briefing.Headline, Style = (Style)FindResource("H2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        // The AI summary, when on, in an accent-edged box that fills in when it arrives.
        if (summarise is not null)
        {
            _summary.Text = "Writing a plan for the day…";
            _summary.Foreground = Brand.Muted;
            page.Children.Add(new Border
            {
                Background = Brand.Brush("AccentSoft"),
                BorderBrush = Brand.Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 16),
                Child = _summary,
            });
            _ = FillSummary(summarise);
        }

        if (briefing.IsEmpty)
        {
            page.Children.Add(new TextBlock
            {
                Text = "Nothing due, nothing late, and nothing booked. A good day to get ahead.",
                Foreground = Brand.Brush("Body"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        foreach (var section in briefing.Sections) page.Children.Add(Section(section));

        if (briefing.Yesterday is { } yesterday)
        {
            page.Children.Add(new TextBlock { Text = yesterday, Foreground = Brand.Brush("AccentInk"), Margin = new Thickness(0, 4, 0, 0) });
        }

        var done = new Button { Content = "Done", Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        done.Click += (_, _) => Close();
        var open = new Button { Content = "Open TeezyFlow", Style = (Style)FindResource("Secondary"), Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => { openApp(); Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (speak is not null)
        {
            var read = new Button { Content = "Read it to me", Style = (Style)FindResource("Quiet") };
            read.Click += (_, _) => speak(Spoken(briefing));
            buttons.Children.Add(read);
        }
        buttons.Children.Add(open);
        buttons.Children.Add(done);
        page.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
    }

    private string Spoken(Briefing briefing) =>
        _summary.Text is { Length: > 0 } s && _summary.Foreground != Brand.Muted
            ? $"{MorningBriefing.Spoken(briefing)} {s}"
            : MorningBriefing.Spoken(briefing);

    private async Task FillSummary(Func<Task<string?>> summarise)
    {
        try
        {
            var text = await summarise();
            _summary.Text = text ?? "No summary came back today.";
            _summary.Foreground = text is null ? Brand.Muted : Brand.Ink;
        }
        catch (AssistantUnavailableException problem)
        {
            _summary.Text = problem.Message;
            _summary.Foreground = Brand.Muted;
        }
    }

    private Border Section(BriefingSection section)
    {
        var rows = new StackPanel();
        for (var i = 0; i < section.Items.Count; i++) rows.Children.Add(Row(section.Items[i], first: i == 0));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{section.Title.ToUpperInvariant()}  {section.Items.Count}",
            Style = (Style)FindResource("SectionLabel"),
            Foreground = section.Warning ? Brand.Brush("CautionBorder") : Brand.Muted,
            Margin = new Thickness(0, 0, 0, 6),
        });
        stack.Children.Add(new Border { Style = (Style)FindResource("ListCard"), Child = rows });

        return new Border { Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    private Border Row(BriefingItem item, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement lead;
        if (item.TaskId is { } id)
        {
            var tick = new CheckBox { Style = (Style)FindResource("Tick"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Close" };
            tick.Checked += (_, _) => _tasks.Close(id);
            tick.Unchecked += (_, _) => _tasks.Reopen(id);
            lead = tick;
        }
        else
        {
            lead = new Border
            {
                Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Margin = new Thickness(5, 0, 14, 0),
                Background = item.Meeting ? Brand.Brush("Info") : Brand.Accent, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var text = new TextBlock { Text = item.Text, Foreground = Brand.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock
        {
            Text = item.Detail ?? string.Empty,
            FontSize = 12,
            Foreground = item.Late ? Brand.Brush("CautionBorder") : Brand.Muted,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(lead);
        grid.Children.Add(text);
        grid.Children.Add(detail);

        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = Brushes.Transparent,
            Child = grid,
        };

        if (item.TaskId is { } taskId)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = Brand.Brush("Raised");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && Within(source, lead)) return;
                _openTask(taskId);
            };
        }

        return row;
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
}
