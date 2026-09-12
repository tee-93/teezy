using System;
using System.Windows;
using System.Windows.Input;
using Teezy.Core;
using Teezy.Core.Mail;

namespace Teezy.App;

/// <summary>One message from the inbox card, read in place.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow(MailMessage message, string mailbox)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        Mailbox.Text = mailbox;
        When.Text = $"{Spoken.LongDate(message.Received, DateTimeOffset.Now)} "
                    + Spoken.Clock(message.Received);

        Subject.Text = message.Subject;
        Who.Text = message.Who;
        Address.Text = message.FromAddress;

        Preview.Text = message.Preview is { Length: > 0 } preview
            ? preview
            : "No preview — the message had no readable text, or the provider did not send one.";
    }

    /// <summary>Escape closes the reader, the way every other read-and-dismiss window does.</summary>
    /// <remarks>
    /// Handled here rather than with <c>IsCancel</c> on the button. That is the usual way to get
    /// Escape on a dialog, but it works through the access-key manager and depends on where
    /// focus happens to be; this window has nothing focusable in it worth landing on, so the
    /// key is taken directly.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
