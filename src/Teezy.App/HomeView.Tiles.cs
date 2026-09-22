using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Home;

namespace Teezy.App;

/// <summary>Home ▸ the tiles across the top, and Customise.</summary>
/// <remarks>
/// After Pursiva's widget bar: a label in small capitals, one big figure, a line under it; the
/// whole tile opens what it counts. Equal columns, up to five, wrapping on a narrow window.
/// </remarks>
public partial class HomeView
{
    private int _tileCount;

    private void RenderTiles(HomeSnapshot snapshot, TeezySettings settings)
    {
        var keys = HomeLayout.Sanitise(settings.HomeTiles, HomeLayout.Tiles, HomeLayout.DefaultTiles,
            CalendarConnected, MailConnected, HomeLayout.MaxTiles);

        TilesHost.Children.Clear();
        foreach (var key in keys)
        {
            if (HomeLayout.Find(key) is not { } part) continue;
            TilesHost.Children.Add(Tile(part, HomeTiles.Compute(key, snapshot)));
        }

        _tileCount = TilesHost.Children.Count;
        ArrangeTiles(PageGrid.ActualWidth);
    }

    /// <summary>All in one row when there is room; three, then two, per row as the window narrows.</summary>
    private void ArrangeTiles(double width)
    {
        if (_tileCount == 0) return;
        var perRow = width switch
        {
            <= 0 or >= 1000 => _tileCount,
            >= 640 => Math.Min(3, _tileCount),
            _ => Math.Min(2, _tileCount),
        };
        TilesHost.Columns = perRow;
        TilesHost.Rows = (int)Math.Ceiling(_tileCount / (double)perRow);
    }

    private Border Tile(HomePart part, TileResult result)
    {
        var value = new TextBlock
        {
            Text = result.Value,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = result.Tone switch
            {
                TileTone.Good => Brand.Brush("AccentInk"),
                TileTone.Warning => Brand.Brush("CautionBorder"),
                TileTone.Bad => Brand.Brush("Danger"),
                _ => Brand.Ink,
            },
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = part.Label.ToUpper(Display),
            Style = (Style)FindResource("SectionLabel"),
            Margin = new Thickness(0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(value);
        body.Children.Add(new TextBlock
        {
            Text = result.Caption,
            FontSize = 12,
            Foreground = Brand.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var tile = new Border
        {
            Background = Brand.Brush("Card"),
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(6, 0, 6, 12),
            Child = body,
            ToolTip = part.Description,
        };

        if (result.Target != TileTarget.None)
        {
            tile.Cursor = Cursors.Hand;
            tile.MouseEnter += (_, _) => { tile.Background = Brand.Brush("Raised"); tile.BorderBrush = Brand.Accent; };
            tile.MouseLeave += (_, _) => { tile.Background = Brand.Brush("Card"); tile.BorderBrush = Brand.Brush("Hairline"); };
            tile.MouseLeftButtonUp += (_, _) => Go(result);
        }

        return tile;
    }

    private void Go(TileResult result)
    {
        switch (result.Target)
        {
            case TileTarget.Task when result.TaskId is { } id: _actions.OpenTask(id); break;
            case TileTarget.Tasks: _actions.OpenPage(Page.Tasks); break;
            case TileTarget.Insights: _actions.OpenPage(Page.Insights); break;
            case TileTarget.Meetings: _actions.OpenPage(Page.Meetings); break;
        }
    }

    // ---- Customise ----

    private void OnCustomise(object sender, RoutedEventArgs e)
    {
        var open = CustomisePanel.Visibility != Visibility.Visible;
        CustomisePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CustomiseButton.Content = open ? "Done" : "Customise";
        if (open) RenderCustomise(_settings());
    }

    private void RenderCustomise(TeezySettings settings)
    {
        Section(CustomiseTiles, "TILES", $"Up to {HomeLayout.MaxTiles}", HomeLayout.Tiles, HomeLayout.DefaultTiles,
            settings.HomeTiles, HomeLayout.MaxTiles, (s, keys) => s with { HomeTiles = keys });
        Section(CustomiseLeft, "LEFT COLUMN", "Today and the week", HomeLayout.LeftPanels, HomeLayout.DefaultLeft,
            settings.HomeLeft, int.MaxValue, (s, keys) => s with { HomeLeft = keys });
        Section(CustomiseRight, "RIGHT COLUMN", "What is coming, and coming in", HomeLayout.RightPanels, HomeLayout.DefaultRight,
            settings.HomeRight, int.MaxValue, (s, keys) => s with { HomeRight = keys });

        CustomiseAccounts.Visibility = CalendarConnected && MailConnected ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One list: the chosen parts in order, with ↑ ↓, then the rest to tick on.</summary>
    private void Section(
        StackPanel host, string title, string hint, IReadOnlyList<HomePart> catalogue, IReadOnlyList<string> defaults,
        IReadOnlyList<string> saved, int max, Func<TeezySettings, IReadOnlyList<string>, TeezySettings> store)
    {
        host.Children.Clear();
        host.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 0, 0, 2) });
        host.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("FormHint"), Margin = new Thickness(0, 0, 0, 8) });

        var chosen = HomeLayout.Sanitise(saved, catalogue, defaults, CalendarConnected, MailConnected, max).ToList();
        var others = catalogue.Where(p => p.IsAvailable(CalendarConnected, MailConnected) && !chosen.Contains(p.Key)).ToList();

        void Save(List<string> keys)
        {
            var settings = _settings();
            _save(store(settings, HomeLayout.Merge(keys, saved, catalogue, CalendarConnected, MailConnected)));
            Render();
        }

        for (var i = 0; i < chosen.Count; i++)
        {
            var index = i;
            var part = catalogue.First(p => p.Key == chosen[index]);
            host.Children.Add(Row(part, true, enabled: chosen.Count > 1,
                toggle: () => { var keys = chosen.ToList(); keys.RemoveAt(index); Save(keys); },
                up: index > 0 ? () => { var keys = chosen.ToList(); (keys[index - 1], keys[index]) = (keys[index], keys[index - 1]); Save(keys); } : null,
                down: index < chosen.Count - 1 ? () => { var keys = chosen.ToList(); (keys[index + 1], keys[index]) = (keys[index], keys[index + 1]); Save(keys); } : null));
        }

        foreach (var part in others)
        {
            host.Children.Add(Row(part, false, enabled: chosen.Count < max,
                toggle: () => Save([.. chosen, part.Key]), up: null, down: null));
        }
    }

    /// <summary>A row in Customise: tick, name and one line, and ↑ ↓ for the chosen.</summary>
    private Border Row(HomePart part, bool chosen, bool enabled, Action toggle, Action? up, Action? down)
    {
        var tick = new CheckBox
        {
            Style = (Style)FindResource("Tick"),
            IsChecked = chosen,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = chosen ? (enabled ? "Hide" : "At least one stays") : (enabled ? "Show" : "The row is full — untick one first"),
        };
        tick.Click += (_, _) => toggle();

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = part.Label, Foreground = chosen ? Brand.Ink : Brand.Brush("Body"), FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal });
        text.Children.Add(new TextBlock { Text = part.Description, FontSize = 11, Foreground = Brand.Muted, TextTrimming = TextTrimming.CharacterEllipsis });

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (chosen)
        {
            arrows.Children.Add(Arrow("↑", "Move up", up));
            arrows.Children.Add(Arrow("↓", "Move down", down));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(arrows, 2);
        grid.Children.Add(tick);
        grid.Children.Add(text);
        grid.Children.Add(arrows);

        return new Border
        {
            Background = chosen ? Brand.Brush("AccentSoft") : Brushes.Transparent,
            BorderBrush = chosen ? Brand.Accent : Brand.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 6, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Opacity = enabled || chosen ? 1 : 0.45,
            Child = grid,
        };
    }

    private Button Arrow(string glyph, string tip, Action? run)
    {
        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("Quiet"),
            Padding = new Thickness(7, 0, 7, 0),
            ToolTip = tip,
            IsEnabled = run is not null,
        };
        if (run is not null) button.Click += (_, _) => run();
        return button;
    }
}
