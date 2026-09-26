using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>A date and time picker: a field that opens a month calendar, quick days and a time list.</summary>
/// <remarks>
/// <para>
/// Built rather than taken from WPF: the stock <c>DatePicker</c> and <c>Calendar</c> are drawn in
/// Windows 7 light grey, and restyling them fully is more code than this.
/// </para>
/// <para>
/// Weeks start on Monday, as they do at work in Australia. Every change applies at once and raises
/// <see cref="Changed"/>; the popup closes on Done or a click elsewhere.
/// </para>
/// </remarks>
public sealed class DateTimeField : UserControl
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _clear;
    private readonly Popup _popup;
    private readonly TextBlock _month = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private readonly ComboBox _time = new() { Width = 130 };
    private DateOnly _shown;
    private bool _filling;

    /// <summary>Raised whenever the user changes the value.</summary>
    public event Action? Changed;

    /// <summary>The chosen day, or null for none.</summary>
    public DateOnly? Date { get; private set; }

    /// <summary>The chosen time, or null for "any time that day".</summary>
    public TimeOnly? Time { get; private set; }

    /// <summary>A reminder always has a time; choosing a day gives it one if it has none.</summary>
    public bool TimeRequired { get; set; }

    /// <summary>The time a day gets when <see cref="TimeRequired"/> and none is set.</summary>
    public TimeOnly DefaultTime { get; set; } = new(9, 0);

    /// <summary>
    /// Whether a time can be chosen as well as a day. False for a date that is only ever a day —
    /// the day a quote went out has no o'clock about it.
    /// </summary>
    public bool ShowTime { get; set; } = true;

    /// <summary>What the field says when empty.</summary>
    public string Placeholder { get; set; } = "No date";

    public DateTimeField()
    {
        var field = new Border
        {
            Background = Brush("Sunken"),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Height = 30,
            Padding = new Thickness(8, 0, 4, 0),
            Cursor = Cursors.Hand,
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,4 L13,4 L13,13 L3,13 Z M3,7 L13,7 M6,2 L6,5 M10,2 L10,5"),
            Stroke = Brush("Muted"),
            StrokeThickness = 1.2,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clear = new Button
        {
            Content = "✕",
            Style = Styled("Quiet"),
            Padding = new Thickness(6, 0, 6, 0),
            MinHeight = 22,
            ToolTip = "Clear",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _clear.Click += (_, e) => { e.Handled = true; Apply(null, null); };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(_clear, 2);
        row.Children.Add(icon);
        row.Children.Add(_text);
        row.Children.Add(_clear);
        field.Child = row;

        field.MouseEnter += (_, _) => field.BorderBrush = Brush("Accent");
        field.MouseLeave += (_, _) => field.BorderBrush = _popup is { IsOpen: true } ? Brush("Accent") : Brush("Hairline");
        field.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Within(source, _clear)) return;
            Open();
        };

        _popup = new Popup
        {
            PlacementTarget = field,

            // Custom, not Bottom: on a touch-screen laptop Windows right-aligns pop-ups for a
            // right-handed user, which threw this one leftwards across the task list.
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (_, target, _) =>
                [new CustomPopupPlacement(new Point(0, target.Height), PopupPrimaryAxis.Horizontal)],
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = BuildPanel(),
        };
        _popup.Closed += (_, _) => field.BorderBrush = Brush("Hairline");

        var host = new Grid();
        host.Children.Add(field);
        host.Children.Add(_popup);
        Content = host;

        Loaded += (_, _) => ShowText();
    }

    /// <summary>Sets the value from the task, without raising <see cref="Changed"/>.</summary>
    public void Set(DateOnly? date, TimeOnly? time)
    {
        Date = date;
        Time = date is null ? null : time;
        ShowText();
    }

    /// <summary>Sets the value from a moment, for a reminder.</summary>
    public void Set(DateTimeOffset? at) =>
        Set(at is { } a ? DateOnly.FromDateTime(a.LocalDateTime) : null,
            at is { } b ? TimeOnly.FromDateTime(b.LocalDateTime) : null);

    /// <summary>The value as a moment, for a reminder; null when empty.</summary>
    public DateTimeOffset? Moment => Date is { } day ? TaskPlan.At(day, Time ?? DefaultTime) : null;

    // ---- the field ----

    private void ShowText()
    {
        if (Date is not { } day)
        {
            _text.Text = Placeholder;
            _text.Foreground = Brush("Faint");
            _clear.Visibility = Visibility.Collapsed;
            return;
        }

        _text.Text = Time is { } t ? $"{TasksView.Day(day)}, {TasksView.Clock(t)}" : TasksView.Day(day);
        _text.Foreground = Brush("Ink");
        _clear.Visibility = Visibility.Visible;
    }

    private void Apply(DateOnly? date, TimeOnly? time)
    {
        if (date is not null && time is null && TimeRequired) time = DefaultTime;
        Date = date;
        Time = date is null ? null : time;
        ShowText();
        if (_popup.IsOpen) Fill();
        Changed?.Invoke();
    }

    private void Open()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var basis = Date ?? today;
        _shown = new DateOnly(basis.Year, basis.Month, 1);
        Fill();
        _popup.IsOpen = true;
    }

    // ---- the popup ----

    private Border BuildPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };

        // Quick days first: most dates set here are one of these.
        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, day) in new (string, Func<DateOnly>)[]
                 {
                     ("Today", () => DateOnly.FromDateTime(DateTime.Today)),
                     ("Tomorrow", () => DateOnly.FromDateTime(DateTime.Today).AddDays(1)),
                     ("Next Mon", () => NextMonday()),
                     ("In a week", () => TaskPlan.Workday(DateOnly.FromDateTime(DateTime.Today).AddDays(7))),
                 })
        {
            var button = new Button { Content = label, Style = Styled("Secondary"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2), MinHeight = 24, FontSize = 12 };
            button.Click += (_, _) =>
            {
                var chosen = day();
                _shown = new DateOnly(chosen.Year, chosen.Month, 1);
                Apply(chosen, Time);
            };
            quick.Children.Add(button);
        }
        panel.Children.Add(quick);

        // The month, with arrows either side.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = new Button { Content = "‹", Style = Styled("Quiet"), FontSize = 16, Padding = new Thickness(10, 0, 10, 2) };
        var forward = new Button { Content = "›", Style = Styled("Quiet"), FontSize = 16, Padding = new Thickness(10, 0, 10, 2) };
        back.Click += (_, _) => { _shown = _shown.AddMonths(-1); Fill(); };
        forward.Click += (_, _) => { _shown = _shown.AddMonths(1); Fill(); };
        _month.Foreground = Brush("Ink");
        Grid.SetColumn(_month, 1);
        Grid.SetColumn(forward, 2);
        header.Children.Add(back);
        header.Children.Add(_month);
        header.Children.Add(forward);
        panel.Children.Add(header);

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var name in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
        {
            weekdays.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Brush("Faint"), HorizontalAlignment = HorizontalAlignment.Center });
        }
        panel.Children.Add(weekdays);
        panel.Children.Add(_days);

        // The time, then Done.
        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var timeLabel = new TextBlock { Text = "Time", Foreground = Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        bottom.Children.Add(timeLabel);
        Loaded += (_, _) =>
        {
            var shown = ShowTime ? Visibility.Visible : Visibility.Collapsed;
            timeLabel.Visibility = shown;
            _time.Visibility = shown;
        };
        Grid.SetColumn(_time, 1);
        _time.HorizontalAlignment = HorizontalAlignment.Left;
        _time.SelectionChanged += (_, _) => OnTimeChosen();
        bottom.Children.Add(_time);
        var done = new Button { Content = "Done", Style = Styled("Primary"), Margin = new Thickness(10, 0, 0, 0) };
        done.Click += (_, _) => _popup.IsOpen = false;
        Grid.SetColumn(done, 2);
        bottom.Children.Add(done);
        panel.Children.Add(bottom);

        return new Border
        {
            Background = Brush("Raised"),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 14, 16),
            Width = 300,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 6, Direction = 270, Opacity = 0.45 },
            Child = panel,
        };
    }

    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var ahead = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(ahead == 0 ? 7 : ahead);
    }

    private void Fill()
    {
        _filling = true;
        _month.Text = _shown.ToString("MMMM yyyy", Display);

        _days.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var offset = ((int)_shown.DayOfWeek + 6) % 7; // Monday first
        var first = _shown.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            _days.Children.Add(DayCell(day, inMonth: day.Month == _shown.Month, today));
        }

        // Times in quarter hours, 6 am to 9:45 pm, plus whatever is set if it falls outside.
        _time.Items.Clear();
        var times = new List<TimeOnly?>();
        if (!TimeRequired) times.Add(null);
        for (var minutes = 6 * 60; minutes < 22 * 60; minutes += 15) times.Add(new TimeOnly(minutes / 60, minutes % 60));
        if (Time is { } current && !times.Contains(current)) times.Add(current);
        times = [.. times.OrderBy(t => t ?? TimeOnly.MinValue)];

        foreach (var time in times) _time.Items.Add(new ComboBoxItem { Content = time is { } t ? TasksView.Clock(t) : "Any time", Tag = time });
        var selected = times.IndexOf(Time ?? (TimeRequired && Date is not null ? DefaultTime : null));
        _time.SelectedIndex = selected;
        _time.IsEnabled = Date is not null;
        _filling = false;
    }

    private Border DayCell(DateOnly day, bool inMonth, DateOnly today)
    {
        var chosen = day == Date;
        var cell = new Border
        {
            Height = 30,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = chosen ? Brush("Accent") : Brushes.Transparent,
            BorderBrush = day == today && !chosen ? Brush("Accent") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.InvariantCulture),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = chosen ? Brush("OnAccent") : inMonth ? Brush("Ink") : Brush("Faint"),
            },
        };

        if (!chosen)
        {
            cell.MouseEnter += (_, _) => cell.Background = Brush("Selected");
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        }

        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (!inMonth) _shown = new DateOnly(day.Year, day.Month, 1);
            Apply(day, Time);
        };
        return cell;
    }

    private void OnTimeChosen()
    {
        if (_filling || Date is null || _time.SelectedItem is not ComboBoxItem { } item) return;
        var time = item.Tag as TimeOnly?;
        if (time == Time) return;
        Apply(Date, time);
    }

    // ---- helpers ----

    private static bool Within(DependencyObject child, DependencyObject parent)
    {
        for (var node = child; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == parent) return true;
        }
        return false;
    }

    private static Brush Brush(string key) => Brand.Brush(key);

    private static Style Styled(string key) => (Style)Application.Current.FindResource(key);
}
