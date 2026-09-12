using System;
using System.Windows;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
