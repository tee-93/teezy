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

/// <summary>Everything dictated, searchable.</summary>
/// <remarks>
/// Its own page rather than the bottom half of the dashboard. The dashboard answers "what is
/// going on"; this answers "where did that text go", and sharing one screen left neither with
/// room. It is also the recovery page — text goes into somebody else's application, and when
/// that application eats it this is the only place it still exists.
/// </remarks>
public partial class TranscriptsView : UserControl
{
    private readonly HistoryStore _history;
    private IReadOnlyList<HistoryEntry> _all = [];

    public TranscriptsView(HistoryStore history)
    {
        InitializeComponent();
        _history = history;
        Refresh();
    }

    public void Refresh()
    {
        _all = _history.Load();
        ApplyFilter(SearchBox.Text);

        Subtitle.Text = _all.Count == 0
            ? "Hold your push-to-talk key anywhere to start."
            : $"{_all.Count:N0} dictations, searchable and kept on this machine.";
    }

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
        EmptyTitle.Text = searching ? "Nothing matches" : "Nothing dictated yet";
        EmptyHint.Text = searching
            ? "Try a shorter word, or part of one."
            : "Hold your push-to-talk key anywhere, say something, and let go.";
    }

    private static List<HistoryGroup> Group(IReadOnlyList<HistoryEntry> entries)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return
        [
            .. entries
                .GroupBy(e => DateOnly.FromDateTime(e.At.ToLocalTime().Date))
                .OrderByDescending(g => g.Key)
                .Select(g => new HistoryGroup(
                    HeaderFor(g.Key, today),
                    [.. g.OrderByDescending(e => e.At).Select(ToRow)])),
        ];
    }

    private static string HeaderFor(DateOnly day, DateOnly today)
    {
        var ago = today.DayNumber - day.DayNumber;

        return ago switch
        {
            0 => "TODAY",
            1 => "YESTERDAY",
            < 7 => day.ToString("dddd", CultureInfo.CurrentCulture).ToUpperInvariant(),
            _ => day.ToString("d MMMM", CultureInfo.CurrentCulture).ToUpperInvariant(),
        };
    }

    private static HistoryRow ToRow(HistoryEntry e)
    {
        var words = e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var meta = string.IsNullOrWhiteSpace(e.App)
            ? $"{words} words"
            : $"{e.App} · {words} words";

        return new HistoryRow(
            e.At.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture).ToLowerInvariant(),
            e.Text,
            meta,
            e);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow row) return;

        try
        {
            Clipboard.SetText(row.Entry.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process had the clipboard open. Nothing useful to say and nothing to fix;
            // failing to copy must not take the window down.
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow row) return;

        _history.Delete(row.Entry.Id);
        Refresh();
    }
}
