using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>The quotes you have out: what to chase, what has gone quiet, and what the month came to.</summary>
/// <remarks>
/// <para>
/// A quote is the job; a chase is a task. So this page never grows its own reminders, its own
/// list of what is due today, or its own place on Home — it records the quote and lets
/// <see cref="QuoteChasing"/> book the chases into the task list, where all of that already works.
/// </para>
/// <para>
/// Four ways in, because a quote is made in four different situations: typed at the desk,
/// dragged in as the email that sent it, spoken in the car, or imported from the CRM in bulk.
/// </para>
/// </remarks>
public partial class QuotesView : UserControl
{
    private readonly QuoteStore _store;
    private readonly TaskStore _tasks;
    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _saveSettings;
    private readonly Action<string>? _openTask;

    private string? _selected;
    private bool _filling;
    private Teezy.Core.Tasks.DroppedEmail? _pendingEmail;
    private Action? _noticeAction;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public QuotesView(
        QuoteStore store,
        TaskStore tasks,
        Func<TeezySettings> settings,
        Action<TeezySettings> saveSettings,
        Action<string>? openTask = null)
    {
        InitializeComponent();
        _store = store;
        _tasks = tasks;
        _settings = settings;
        _saveSettings = saveSettings;
        _openTask = openTask;

        SentField.ShowTime = false;
        SentField.Placeholder = "Not said";
        SentField.Changed += () =>
        {
            if (_filling || Chosen() is not { } quote || SentField.Date is not { } day) return;
            _store.Update(quote with { Sent = day });
            Refresh();
        };

        _store.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Refresh(); });
        Unloaded += (_, _) => SavePendingNote();
        Refresh();
    }

    public void Refresh()
    {
        var quotes = _store.Visible;
        var totals = QuotePlan.Totals(quotes, Today);

        OpenValue.Text = QuotePlan.Money(totals.Open.Value);
        OpenCount.Text = totals.Open.Count switch
        {
            0 => "Nothing out at the moment",
            1 => "1 quote open",
            _ => $"{totals.Open.Count} quotes open",
        };

        WonValue.Text = QuotePlan.Money(totals.Won.Value);
        WonCount.Text = totals.Won.Count == 1 ? "1 quote" : $"{totals.Won.Count} quotes";

        WinRate.Text = totals.WinRate is { } rate
            ? rate.ToString("P0", CultureInfo.CurrentCulture)
            : "—";
        WinRateHint.Text = totals.WinRate is null
            ? "Nothing decided this month"
            : $"{totals.Won.Count} won, {totals.Lost.Count} lost";

        var due = QuotePlan.DueToChase(quotes, Today).Count;
        Lede.Text = quotes.Count == 0
            ? "Every quote you send, and the chasing that follows it."
            : due == 0
                ? "Nothing to chase today."
                : due == 1 ? "1 quote to chase today." : $"{due} quotes to chase today.";

        ShowGroups(quotes);
        ShowDetail();
    }

    /// <summary>Opens the page at one quote.</summary>
    public void Select(string id)
    {
        _selected = id;
        Refresh();
    }

    // ---- the list ----

    private void ShowGroups(IReadOnlyList<Quote> quotes)
    {
        Groups.Children.Clear();
        EmptyState.Visibility = quotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (bucket, rows) in QuotePlan.Arrange(quotes, Today))
        {
            var list = new StackPanel();
            for (var i = 0; i < rows.Count; i++) list.Children.Add(Row(rows[i], first: i == 0));

            var header = new Grid { Margin = new Thickness(2, 0, 2, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = $"{Title(bucket).ToUpperInvariant()}  {rows.Count}",
                Style = (Style)FindResource("SectionLabel"),
                Foreground = bucket switch
                {
                    QuoteBucket.ToChase => Brand.Brush("CautionBorder"),
                    QuoteBucket.Quiet => Brand.Brush("Muted"),
                    _ => Brand.Muted,
                },
            };

            var value = new TextBlock
            {
                Text = QuotePlan.Money(rows.Sum(q => q.Amount)),
                FontSize = 12,
                Foreground = Brand.Muted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            header.Children.Add(title);
            header.Children.Add(value);

            Groups.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 16),
                Children =
                {
                    header,
                    new Border { Style = (Style)FindResource("ListCard"), Child = list },
                },
            });
        }
    }

    private static string Title(QuoteBucket bucket) => bucket switch
    {
        QuoteBucket.ToChase => "Chase today",
        QuoteBucket.Quiet => "Gone quiet",
        QuoteBucket.Open => "Out there",
        _ => "Decided",
    };

    private Border Row(Quote quote, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = new TextBlock
        {
            Foreground = quote.IsOpen ? Brand.Ink : Brand.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        head.Inlines.Add(new System.Windows.Documents.Run(quote.Customer) { FontWeight = FontWeights.SemiBold });
        if (quote.What.Length > 0) head.Inlines.Add(new System.Windows.Documents.Run($" — {quote.What}"));

        var meta = new TextBlock
        {
            Text = Meta(quote),
            FontSize = 12,
            Foreground = QuotePlan.BucketOf(quote, Today) == QuoteBucket.ToChase
                ? Brand.Brush("CautionBorder")
                : Brand.Muted,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(head);
        text.Children.Add(meta);

        var money = new TextBlock
        {
            Text = QuotePlan.Money(quote.Amount),
            Foreground = quote.Status == QuoteStatus.Won ? Brand.Brush("AccentInk") : Brand.Ink,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(money, FontNumeralAlignment.Tabular);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (quote.IsOpen)
        {
            var won = new Button { Content = "Won", Style = (Style)FindResource("Quiet"), Tag = quote.Id, ToolTip = "Won today" };
            won.Click += (_, _) => Decide(quote.Id, QuoteStatus.Won);
            var lost = new Button { Content = "Lost", Style = (Style)FindResource("Quiet"), Tag = quote.Id, ToolTip = "Lost today" };
            lost.Click += (_, _) => Decide(quote.Id, QuoteStatus.Lost);
            buttons.Children.Add(won);
            buttons.Children.Add(lost);
        }

        Grid.SetColumn(money, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(text);
        grid.Children.Add(money);
        grid.Children.Add(buttons);

        var row = new Border
        {
            Padding = new Thickness(12, 9, 8, 9),
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = quote.Id == _selected ? Brand.Brush("Selected") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = quote.Id,
        };
        row.Child = grid;

        if (quote.Id != _selected)
        {
            row.MouseEnter += (_, _) => row.Background = Brand.Brush("Raised");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }

        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Within<Button>(source)) return;
            SavePendingNote();
            _selected = quote.Id;
            Refresh();
        };

        return row;
    }

    /// <summary>The grey line under a quote: how long it has been out, and what happens next.</summary>
    private string Meta(Quote quote)
    {
        var parts = new List<string> { $"sent {TasksView.Day(quote.Sent)}" };

        if (quote.Status != QuoteStatus.Open)
        {
            parts.Add(quote.Status == QuoteStatus.Won ? "won" : "lost");
            if (quote.Decided is { } decided) parts[^1] += $" {TasksView.Day(decided)}";
            return string.Join(" · ", parts);
        }

        parts.Add(quote.Chased switch
        {
            0 => "not chased yet",
            1 => "chased once",
            _ => $"chased {quote.Chased}×",
        });

        if (QuotePlan.NextChase(quote) is { } next)
        {
            parts.Add(next <= Today ? "chase due" : $"next chase {TasksView.Day(next)}");
        }

        if (QuotePlan.IsQuiet(quote, Today))
        {
            parts.Add($"quiet for {Today.DayNumber - quote.LastMoved.DayNumber} days");
        }

        if (quote.Reference is { Length: > 0 } reference) parts.Add(reference);

        return string.Join(" · ", parts);
    }

    private Quote? Chosen() => _selected is { } id ? _store.Find(id) : null;

    private static bool Within<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? at = node; at is not null;
             at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is T) return true;
        }

        return false;
    }
}
