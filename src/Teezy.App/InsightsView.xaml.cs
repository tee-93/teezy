using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core.History;

namespace Teezy.App;

/// <summary>One row of the app-usage breakdown.</summary>
public sealed record AppBar(string Name, string Label, double BarWidth);

/// <summary>Aggregate view of everything dictated.</summary>
public partial class InsightsView : UserControl
{
    /// <summary>Weeks of history shown in the heatmap.</summary>
    private const int Weeks = 26;

    private const double CellSize = 13;
    private const double CellGap = 3;

    private readonly HistoryStore _history;

    public InsightsView(HistoryStore history)
    {
        InitializeComponent();
        _history = history;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var stats = UsageStats.From(_history.Load(), today);

        Subtitle.Text = stats.TotalDictations == 0
            ? "Numbers appear here once you have dictated something."
            : $"Across {stats.TotalDictations:N0} dictations, all measured on this machine.";

        Wpm.Text = stats.WordsPerMinute.ToString(CultureInfo.CurrentCulture);
        WpmHint.Text = stats.WordsPerMinute > 0
            ? $"About {stats.WordsPerMinute / (double)UsageStats.AssumedTypingWpm:0.#}x typing speed."
            : "Not enough speech yet.";

        Fixes.Text = stats.TotalCorrections.ToString("N0", CultureInfo.CurrentCulture);
        FixesHint.Text = stats.TotalCorrections > 0
            ? "Spellings your dictionary guaranteed."
            : "Add entries to your dictionary to fix names the model keeps missing.";

        TotalWords.Text = stats.TotalWords.ToString("N0", CultureInfo.CurrentCulture);
        var minutes = Math.Max(0, stats.MinutesSavedVsTyping);
        TotalWordsHint.Text = stats.TotalWords > 0
            ? $"Roughly {(minutes >= 60 ? $"{minutes / 60:0.#} hours" : $"{minutes:0} minutes")} "
              + $"saved against typing at {UsageStats.AssumedTypingWpm} wpm."
            : string.Empty;

        BuildAppBars(stats);
        BuildHeatmap(stats, today);

        StreakNumber.Text = stats.CurrentStreak.ToString(CultureInfo.CurrentCulture);
        LongestStreak.Text = $"LONGEST · {stats.LongestStreak}";
    }

    private void BuildAppBars(UsageStats stats)
    {
        AppCount.Text = $"{stats.Apps.Count} APPS";

        if (stats.Apps.Count == 0)
        {
            AppBars.ItemsSource = null;
            AppsEmpty.Visibility = Visibility.Visible;
            return;
        }

        AppsEmpty.Visibility = Visibility.Collapsed;

        // Bars are scaled against the busiest app, not against the total. Against the total,
        // a realistic spread leaves every bar a sliver and the chart says nothing.
        var top = stats.Apps.Max(a => a.Count);
        const double fullWidth = 210;

        AppBars.ItemsSource = stats.Apps
            .Take(7)
            .Select(a => new AppBar(
                a.App,
                $"{a.Fraction * 100:0}%",
                Math.Max(6, fullWidth * a.Count / top)))
            .ToList();
    }

    /// <summary>A GitHub-style activity grid: one column per week, seven rows.</summary>
    private void BuildHeatmap(UsageStats stats, DateOnly today)
    {
        DayLabels.Children.Clear();
        Heatmap.Items.Clear();
        Legend.Children.Clear();

        // Rows are Mon/Wed/Fri labelled only — labelling all seven is unreadable at 13 px.
        for (var i = 0; i < 7; i++)
        {
            DayLabels.Children.Add(new TextBlock
            {
                Text = i is 1 or 3 or 5
                    ? CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(i + 1) % 7]
                    : string.Empty,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Brand.Faint),
                Height = CellSize + CellGap,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var busiest = stats.WordsByDay.Count > 0 ? stats.WordsByDay.Values.Max() : 0;

        // Start on the Monday of the week containing the first day shown, so columns are
        // whole weeks and rows line up with weekdays.
        var start = today.AddDays(-(Weeks * 7 - 1));
        start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));

        for (var week = 0; week < Weeks; week++)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, CellGap, 0) };

            for (var day = 0; day < 7; day++)
            {
                var date = start.AddDays(week * 7 + day);
                var words = stats.WordsByDay.GetValueOrDefault(date);

                column.Children.Add(new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 0, CellGap),
                    Background = new SolidColorBrush(
                        date > today ? Colors.Transparent : ShadeFor(words, busiest)),
                    ToolTip = date > today
                        ? null
                        : $"{date.ToDateTime(TimeOnly.MinValue):d MMM yyyy} — "
                          + (words == 0 ? "nothing" : $"{words:N0} words"),
                });
            }

            Heatmap.Items.Add(column);
        }

        foreach (var level in new[] { 0, 1, 2, 3, 4 })
        {
            Legend.Children.Add(new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Background = new SolidColorBrush(Shade(level)),
            });
        }
    }

    /// <summary>
    /// Buckets a day's word count into one of five shades.
    /// </summary>
    /// <remarks>
    /// Relative to the busiest day rather than to a fixed threshold, so the grid stays
    /// readable whether the user dictates 50 words a day or 5,000.
    /// </remarks>
    private static Color ShadeFor(int words, int busiest)
    {
        if (words == 0 || busiest == 0) return Shade(0);
        var fraction = words / (double)busiest;
        return Shade(fraction switch
        {
            > 0.66 => 4,
            > 0.33 => 3,
            > 0.10 => 2,
            _ => 1,
        });
    }

    private static Color Shade(int level) => level switch
    {
        0 => Color.FromRgb(0xEE, 0xEA, 0xE3),
        1 => Color.FromRgb(0xC9, 0xDD, 0xEC),
        2 => Color.FromRgb(0x92, 0xBB, 0xD8),
        3 => Color.FromRgb(0x4E, 0x8C, 0xB5),
        _ => Color.FromRgb(0x1E, 0x5F, 0x8E),
    };
}
