using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core;

namespace Teezy.App;

/// <summary>Asked the first time the window is closed: keep TeezyFlow running, or quit it?</summary>
/// <remarks>
/// A real question, because the answer changes what works: dictation, the assistant and task
/// reminders all live in the running app, so quitting stops them until it is opened again.
/// Remembered with a tick; changeable in Settings ▸ Advanced.
/// </remarks>
public sealed class CloseDialog : Window
{
    /// <summary>What was chosen: <see cref="CloseAction.KeepRunning"/> or <see cref="CloseAction.Quit"/>; null if dismissed.</summary>
    public CloseAction? Choice { get; private set; }

    /// <summary>Whether to stop asking.</summary>
    public bool Remember { get; private set; }

    public CloseDialog(Window owner)
    {
        Owner = owner;
        Title = "TeezyFlow";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brand.Brush("Paper");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(new TextBlock { Text = "Keep TeezyFlow running?", Style = (Style)FindResource("H2") });
        panel.Children.Add(new TextBlock
        {
            Text = "Dictation, the assistant and task reminders only work while TeezyFlow is open. Keep running minimises it to the taskbar; Quit closes it until you open it again.",
            Foreground = Brand.Brush("Body"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
        });

        var remember = new CheckBox
        {
            Content = "Remember my choice",
            Foreground = Brand.Brush("Body"),
            Margin = new Thickness(0, 0, 0, 16),
            ToolTip = "Change it later in Settings ▸ Advanced",
        };
        panel.Children.Add(remember);

        var quit = new Button { Content = "Quit", Style = (Style)FindResource("Secondary") };
        var keep = new Button { Content = "Keep running", Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        quit.Click += (_, _) => Done(CloseAction.Quit, remember);
        keep.Click += (_, _) => Done(CloseAction.KeepRunning, remember);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(quit);
        buttons.Children.Add(keep);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private void Done(CloseAction choice, CheckBox remember)
    {
        Choice = choice;
        Remember = remember.IsChecked == true;
        DialogResult = true;
    }
}
