using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Teezy.Core.History;

namespace Teezy.App;

/// <summary>One dictation, shaped for display.</summary>
public sealed record HistoryRow(string Time, string Text, string Meta, HistoryEntry Entry);

/// <summary>A day of dictations under one heading.</summary>
public sealed record HistoryGroup(string Header, IReadOnlyList<HistoryRow> Items);

/// <summary>Recent dictations, newest first, with the headline stats alongside.</summary>
/// <remarks>
/// The point of this page is recovery: text is injected into another app, and if that app
/// ate it, mangled it, or the user simply wants it again, this is the only place it still
/// exists. That is why the list is the page rather than a panel on it.
/// </remarks>
public partial class HomeView : UserControl
{
    private readonly HistoryStore _history;
    private IReadOnlyList<HistoryEntry> _all = [];

    public HomeView(HistoryStore history)
    {
        InitializeComponent();
        _history = history;
        Refresh();
    }

    public void Refresh()
    {
        _all = _history.Load();
        ApplyFilter(SearchBox.Text);
        UpdateStats();
    }

    private void UpdateStats()
    {
        var stats = UsageStats.From(_all, DateOnly.FromDateTime(DateTime.Today));

        Greeting.Text = $"Welcome back, {FirstName()}";
        Subtitle.Text = stats.TotalDictations == 0
            ? "Hold your push-to-talk key anywhere to start."
            : $"{stats.TotalDictations:N0} dictations so far.";

        TotalWords.Text = Compact(stats.TotalWords);
        Wpm.Text = stats.WordsPerMinute.ToString(CultureInfo.CurrentCulture);
        Streak.Text = stats.CurrentStreak.ToString(CultureInfo.CurrentCulture);
        StreakCaption.Text = stats.CurrentStreak == 1 ? "day streak" : "day streak";

        var minutes = Math.Max(0, stats.MinutesSavedVsTyping);
        TimeSaved.Text = minutes >= 60
            ? $"{minutes / 60:0.#} h"
            : $"{minutes:0} min";
        TimeSavedHint.Text =
            $"versus typing at {UsageStats.AssumedTypingWpm} wpm — an upper bound if you type quickly.";
    }

    private static string FirstName()
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name)) return "there";

        // Windows account names are rarely a bare first name: "ada_", "ada.lovelace" and
        // "ada-l" should all greet the same person.
        var cut = name.Split('.', '_', ' ', '-')[0];
        return cut.Length switch
        {
            0 => "there",
            1 => cut.ToUpperInvariant(),
            _ => char.ToUpperInvariant(cut[0]) + cut[1..],
        };
    }

    /// <summary>48,273 reads better as 48.3K in a small card.</summary>
    private static string Compact(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString("N0", CultureInfo.CurrentCulture),
    };

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter(SearchBox.Text);
    }

    private void ApplyFilter(string? query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _all
            : [.. _all.Where(e => e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))];

        Groups.ItemsSource = Group(matches);

        var empty = matches.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!empty) return;

        var searching = !string.IsNullOrWhiteSpace(query);
        EmptyTitle.Text = searching ? "No matches" : "Nothing dictated yet";
        EmptyHint.Text = searching
            ? $"Nothing in your history contains “{query}”."
            : "Everything you dictate is kept here, so you can copy it again later.";
    }

    private static List<HistoryGroup> Group(IReadOnlyList<HistoryEntry> entries)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return [.. entries
            .GroupBy(e => DateOnly.FromDateTime(e.At.LocalDateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryGroup(
                HeaderFor(g.Key, today),
                [.. g.OrderByDescending(e => e.At).Select(ToRow)]))];
    }

    private static string HeaderFor(DateOnly day, DateOnly today)
    {
        if (day == today) return "TODAY";
        if (day == today.AddDays(-1)) return "YESTERDAY";

        // Within the last week the weekday is more useful than the date.
        if (today.DayNumber - day.DayNumber < 7)
            return day.ToDateTime(TimeOnly.MinValue).ToString("dddd", CultureInfo.CurrentCulture).ToUpperInvariant();

        return day.ToDateTime(TimeOnly.MinValue)
            .ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture)
            .ToUpperInvariant();
    }

    private static HistoryRow ToRow(HistoryEntry e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.App)) parts.Add(e.App);
        if (e.WordCount > 0) parts.Add($"{e.WordCount} words");
        if (e.Corrections > 0) parts.Add($"{e.Corrections} correction{(e.Corrections == 1 ? "" : "s")}");

        return new HistoryRow(
            e.At.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture).ToLowerInvariant(),
            e.Text,
            string.Join("  ·  ", parts),
            e);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HistoryRow row) return;
        try
        {
            Clipboard.SetText(row.Entry.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open. Nothing useful to do, and it is
            // certainly not worth an error dialog over a copy button.
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HistoryRow row) return;
        _history.Delete(row.Entry.Id);
        Refresh();
    }
}
