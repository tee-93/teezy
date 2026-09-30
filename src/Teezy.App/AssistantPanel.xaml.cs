using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Teezy.Core;
using Teezy.Core.Meetings;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>
/// The shell's persistent right-hand rail — today's tasks and meetings, a quick way to jot one
/// down, and what has happened lately. One instance lives in <see cref="MainWindow"/>, so it
/// shows the same thing wherever a page is open, rather than being rebuilt per page.
/// </summary>
public partial class AssistantPanel : UserControl
{
    private const double PanelWidth = 260;
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private TaskStore? _tasks;
    private QuoteStore? _quotes;
    private MeetingStore? _meetingStore;
    private Func<TeezySettings>? _settings;
    private Action<TeezySettings>? _saveSettings;
    private Action? _refreshCurrentPage;

    public AssistantPanel()
    {
        InitializeComponent();
    }

    public bool IsOpen { get; private set; }

    /// <summary>Wires the panel to the app's real stores. Called once, from MainWindow.</summary>
    public void Init(
        TaskStore? tasks, QuoteStore? quotes, MeetingStore? meetingStore,
        Func<TeezySettings> settings, Action<TeezySettings> saveSettings, Action refreshCurrentPage)
    {
        _tasks = tasks;
        _quotes = quotes;
        _meetingStore = meetingStore;
        _settings = settings;
        _saveSettings = saveSettings;
        _refreshCurrentPage = refreshCurrentPage;
        Refresh();
    }

    // ---- open / close ----

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(WidthProperty, new DoubleAnimation(Root.ActualWidth, PanelWidth, Ms(280)) { EasingFunction = ease });
        Inner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(220)));
        InnerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(PanelWidth, 0, Ms(280)) { EasingFunction = ease });
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Root.BeginAnimation(WidthProperty, new DoubleAnimation(Root.ActualWidth, 0, Ms(280)) { EasingFunction = ease });
        Inner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Ms(160)));
        InnerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, PanelWidth, Ms(280)) { EasingFunction = ease });
    }

    // ---- status ----

    /// <summary>Mirrors MainWindow's own status-bar signal — not a second, separate one.</summary>
    public void SetModelStatus(bool loaded, string hotkeyHint)
    {
        StatusDot.Fill = loaded ? Brand.Accent : Brand.Faint;
        StatusText.Text = loaded ? "Ready" : "Loading the speech model…";
        HotkeyHint.Text = hotkeyHint;
    }

    // ---- content ----

    public void Refresh()
    {
        ShowToday();
        ShowActivity();
    }

    private void ShowToday()
    {
        TodayList.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var items = new List<(TimeOnly Sort, string Time, string Text)>();

        if (_tasks is not null)
        {
            foreach (var task in _tasks.Visible.Where(t => t.IsOpen && t.Due == today))
            {
                var sort = task.DueTime ?? TimeOnly.MaxValue;
                items.Add((sort, task.DueTime?.ToString("h:mm tt", CultureInfo.CurrentCulture) ?? "Today", task.Title));
            }
        }

        foreach (var meeting in TodaysMeetings(today))
        {
            var when = TimeOnly.FromDateTime(meeting.Info.Started.ToLocalTime().DateTime);
            items.Add((when, when.ToString("h:mm tt", CultureInfo.CurrentCulture), "Meeting"));
        }

        foreach (var (_, time, text) in items.OrderBy(i => i.Sort))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
            row.Children.Add(new TextBlock
            {
                Text = time, FontSize = 11, Foreground = Brand.Muted, Width = 58, VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12.5, Foreground = Brand.Ink, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            TodayList.Children.Add(row);
        }

        TodayEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<MeetingRecord> TodaysMeetings(DateOnly today)
    {
        if (_meetingStore is null) return [];

        try
        {
            return [.. _meetingStore.List().Where(m => DateOnly.FromDateTime(m.Info.Started.ToLocalTime().DateTime) == today)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void ShowActivity()
    {
        ActivityList.Children.Clear();

        var items = new List<(DateTimeOffset When, string Text)>();

        if (_tasks is not null)
        {
            items.AddRange(_tasks.Visible
                .OrderByDescending(t => t.Modified)
                .Take(6)
                .Select(t => (t.Modified, t.Closed is not null ? $"Task done — {t.Title}" : $"Task updated — {t.Title}")));
        }

        if (_quotes is not null)
        {
            items.AddRange(_quotes.Visible
                .OrderByDescending(q => q.Modified)
                .Take(6)
                .Select(q => (q.Modified, QuoteActivity(q))));
        }

        if (_meetingStore is not null)
        {
            try
            {
                items.AddRange(_meetingStore.List()
                    .OrderByDescending(m => m.Info.Started)
                    .Take(6)
                    .Select(m => (m.Info.Started, $"Meeting recorded — {m.Info.Started.ToLocalTime():h:mm tt}")));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nothing worth surfacing here — the meeting list itself will say so.
            }
        }

        foreach (var (when, text) in items.OrderByDescending(i => i.When).Take(4))
        {
            ActivityList.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = Brand.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        ActivityEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string QuoteActivity(Quote quote)
    {
        var money = QuotePlan.Money(quote.Amount, quote.Currency);
        return quote.Status switch
        {
            QuoteStatus.Won => $"Quote won — {quote.Customer} — {money}",
            QuoteStatus.Lost => $"Quote lost — {quote.Customer}",
            _ when quote.Sent is not null => $"Quote sent — {quote.Customer} — {money}",
            _ => $"Quote drafted — {quote.Customer}",
        };
    }

    // ---- quick capture ----

    private void OnCaptureTyped(object sender, TextChangedEventArgs e) =>
        CapturePlaceholder.Visibility = CaptureBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CaptureBox.Clear(); return; }
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        var title = CaptureBox.Text.Trim();
        if (title.Length == 0 || _tasks is null) return;

        _tasks.Add(title, due: DateOnly.FromDateTime(DateTime.Now));
        CaptureBox.Clear();
        _refreshCurrentPage?.Invoke();
    }
}
