using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>An email attached to a task, read in full in its own window rather than squeezed into the panel.</summary>
/// <remarks>
/// Read-only, like <see cref="MessageWindow"/>: nothing here replies, forwards or opens a link.
/// The text is selectable so any part of it can be copied.
/// </remarks>
public sealed class EmailWindow : Window
{
    public EmailWindow(TaskEmail email, Window? owner)
    {
        Owner = owner;
        Title = email.Subject;
        Width = 680;
        Height = 620;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brand.Brush("Paper");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        var display = CultureInfo.GetCultureInfo("en-AU");
        var when = email.Received is { } received
            ? received.LocalDateTime.ToString("dddd d MMMM yyyy, h:mm tt", display)
            : $"Added {email.Added.LocalDateTime.ToString("ddd d MMM, h:mm tt", display)}";

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = email.Subject, Style = (Style)FindResource("H2"), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock
        {
            Text = email.From is { Length: > 0 } from ? $"{from} · {when}" : when,
            Foreground = Brand.Muted,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var body = new TextBox
        {
            Text = email.Body.Length > 0 ? email.Body : "(No text came with this email.)",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brand.Brush("Sunken"),
            Foreground = Brand.Brush("Body"),
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 13,
        };

        var copy = new Button { Content = "Copy text", Style = (Style)FindResource("Secondary") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(body.Text); copy.Content = "Copied"; }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = "Clipboard busy — try again"; }
        };
        var close = new Button { Content = "Close", Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(head);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
