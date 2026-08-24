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
        BuildSpeed(stats);
        BuildSpend(stats);

        StreakNumber.Text = stats.CurrentStreak.ToString(CultureInfo.CurrentCulture);
        LongestStreak.Text = $"LONGEST · {stats.LongestStreak}";
    }

    /// <summary>Where the wait between letting go and seeing text is spent.</summary>
    /// <remarks>
    /// Medians, and the bars are scaled against the slowest stage rather than against the
    /// total — the point is to show which stage dominates, and three slivers next to one long
    /// bar reads faster than three exact proportions of a whole.
    /// </remarks>
    private void BuildSpeed(UsageStats stats)
    {
        if (stats.TimedDictations == 0)
        {
            SpeedCard.Visibility = Visibility.Collapsed;
            return;
        }

        SpeedCard.Visibility = Visibility.Visible;

        var total = stats.MedianTranscribeMs + stats.MedianCleanupMs + stats.MedianInjectMs;
        SpeedHeadline.Text = Duration(total);

        SpeedRealtime.Text = stats.RealtimeFactor > 0
            ? $"{stats.RealtimeFactor:0.#}× REALTIME · {stats.TimedDictations:N0} TIMED"
            : $"{stats.TimedDictations:N0} TIMED";

        var widest = Math.Max(1, Math.Max(stats.MedianTranscribeMs,
            Math.Max(stats.MedianCleanupMs, stats.MedianInjectMs)));

        StageBars.ItemsSource = new[]
        {
            Stage("Transcribe", stats.MedianTranscribeMs, widest),
            Stage("Cleanup", stats.MedianCleanupMs, widest),
            Stage("Type it in", stats.MedianInjectMs, widest),
        };

        var note = "Median per dictation, measured on this machine. Transcribe is your CPU, "
                   + "cleanup is the Claude round trip when that tier is on, and typing it in "
                   + "is the app receiving the text.";

        // The tail is where a timeout hides. A median of 300 ms next to a worst case of six
        // seconds is a completely different story from a median of 300 ms and nothing else.
        if (stats.SlowestCleanupMs > stats.MedianCleanupMs * 3 && stats.SlowestCleanupMs > 1500)
        {
            note += $" Slowest cleanup so far was {Duration(stats.SlowestCleanupMs)} — if that "
                    + "is common, the tier is costing you more than the median suggests.";
        }
        else if (stats.SlowestTranscribeMs > stats.MedianTranscribeMs * 3 && stats.SlowestTranscribeMs > 1500)
        {
            note += $" Slowest transcription so far was {Duration(stats.SlowestTranscribeMs)}.";
        }

        SpeedNote.Text = note;
    }

    private static object Stage(string name, double ms, double widest) => new
    {
        Name = name,
        Label = Duration(ms),
        BarWidth = Math.Max(2, ms / widest * 260),
    };

    /// <summary>Milliseconds below a second, seconds above — nobody reads "4200 ms".</summary>
    private static string Duration(double ms) =>
        ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    /// <summary>Spend on the Claude tier, counted from the tokens the API reported.</summary>
    /// <remarks>
    /// Money is stated in one place and hedged in one place. The figure is what Teezy spent —
    /// it cannot see the rest of the account — and it comes from a local rate table, so the
    /// note says "about" and any call the table could not price is declared rather than
    /// silently dropped from the total.
    /// </remarks>
    private void BuildSpend(UsageStats stats)
    {
        if (stats.ClaudeCalls == 0)
        {
            SpendCard.Visibility = Visibility.Collapsed;
            return;
        }

        SpendCard.Visibility = Visibility.Visible;

        SpendMonth.Text = Money(stats.CostThisMonthUsd);
        SpendTotal.Text = $"{Money(stats.CostUsd)} ALL TIME";
        SpendTokens.Text =
            $"{stats.ClaudeCalls:N0} CALLS · {stats.TotalTokens.Total:N0} TOKENS";

        ModelRows.ItemsSource = stats.Models
            .Select(m => new
            {
                Name = m.Model,
                Detail = $"{m.Calls:N0} calls · {m.Tokens.InputTokens:N0} in / {m.Tokens.OutputTokens:N0} out",
                Cost = m.CostUsd is { } c ? Money(c) : "—",
            })
            .ToList();

        var note = "Counted from the tokens the API reported, priced locally — about right, "
                   + "not your bill. It cannot see anything else on your account.";

        if (stats.UnpricedCalls > 0)
        {
            note += $" {stats.UnpricedCalls:N0} call(s) used a model this build has no price "
                    + "for and are left out of the totals.";
        }

        SpendNote.Text = note;
    }

    /// <summary>Cents are the wrong unit to hide here — a month of dictation can cost 30c.</summary>
    private static string Money(decimal usd) =>
        usd >= 1m
            ? usd.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"{usd * 100:0.#}¢";

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
