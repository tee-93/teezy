using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Teezy.App;

/// <summary>Puts names to the voices TeezyFlow told apart in a meeting.</summary>
/// <remarks>
/// The model can say that two people are two people; it cannot say who they are. So the
/// transcript opens with "Speaker 1" and "Speaker 2" and this is where they become Priya and
/// Dale — written straight into the transcript file, which is the record everything else reads.
/// Giving two of them the same name is allowed, and is the way to mend a voice that was split
/// in two.
/// </remarks>
public sealed class NameVoicesDialog : Window
{
    private readonly List<(string Was, TextBox Box)> _rows = [];

    /// <summary>The new name for each voice, by its current label. Empty until OK is pressed.</summary>
    public IReadOnlyDictionary<string, string> Names { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public NameVoicesDialog(Window owner, IReadOnlyList<string> voices, string meeting)
    {
        Owner = owner;
        Title = "TeezyFlow";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brand.Brush("Paper");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(new TextBlock { Text = "Name the voices", Style = (Style)FindResource("H2") });
        panel.Children.Add(new TextBlock
        {
            Text = $"In the meeting from {meeting}. Two voices given the same name become one person in the transcript.",
            Foreground = Brand.Brush("Body"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
        });

        foreach (var voice in voices)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = voice,
                Foreground = Brand.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var box = new TextBox { Style = (Style)FindResource("Field"), Text = voice };
            box.SelectAll();
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            panel.Children.Add(grid);

            _rows.Add((voice, box));
        }

        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Secondary"), IsCancel = true };
        var save = new Button
        {
            Content = "Save",
            Style = (Style)FindResource("Primary"),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
        };
        save.Click += (_, _) => Done();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _rows.FirstOrDefault().Box?.Focus();
    }

    private void Done()
    {
        // Only what actually changed, and never to nothing: an empty box means "leave it".
        Names = _rows
            .Select(row => (row.Was, Now: row.Box.Text.Trim()))
            .Where(row => row.Now.Length > 0 && !string.Equals(row.Now, row.Was, StringComparison.Ordinal))
            .ToDictionary(row => row.Was, row => row.Now, StringComparer.Ordinal);

        DialogResult = true;
    }
}
