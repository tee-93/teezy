using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Quotes ▸ the panel on the right, the ways in, and the import.</summary>
public partial class QuotesView
{
    // ---- the panel ----

    private void ShowDetail()
    {
        var quote = Chosen();
        if (quote is null)
        {
            _selected = null;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        var changed = Detail.Tag as string != quote.Id;
        if (changed) SavePendingNote();

        Detail.Tag = quote.Id;
        Detail.Visibility = Visibility.Visible;
        _filling = true;

        if (changed)
        {
            NoteBox.Clear();
            DetailScroll.ScrollToTop();
            DeleteButton.Tag = null;
            DeleteButton.Content = "Delete quote";
        }

        if (changed || !Detail.IsKeyboardFocusWithin)
        {
            CustomerBox.Text = quote.Customer;
            WhatBox.Text = quote.What;
            ValueBox.Text = quote.Amount.ToString("0.##", CultureInfo.CurrentCulture);
            ReferenceBox.Text = quote.Reference ?? string.Empty;
            ContactBox.Text = quote.Contact ?? string.Empty;
        }

        SentField.Set(quote.Sent, null);

        DetailState.Text = quote.Status switch
        {
            QuoteStatus.Won => "WON",
            QuoteStatus.Lost => "LOST",
            _ => QuotePlan.BucketOf(quote, Today) switch
            {
                QuoteBucket.ToChase => "CHASE TODAY",
                QuoteBucket.Quiet => "GONE QUIET",
                _ => "OPEN",
            },
        };

        ShowChaseState(quote);
        ShowNotes(quote);
        ShowEmails(quote);

        WonButton.Visibility = quote.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        LostButton.Visibility = quote.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ReopenButton.Visibility = quote.IsOpen ? Visibility.Collapsed : Visibility.Visible;

        _filling = false;
    }

    private void ShowChaseState(Quote quote)
    {
        var chase = quote.ChaseTaskId is { Length: > 0 } id ? _tasks.Find(id) : null;

        ChaseState.Text = quote switch
        {
            { Status: QuoteStatus.Won } => "Won — nothing is chasing it now.",
            { Status: QuoteStatus.Lost } => "Lost — nothing is chasing it now.",
            _ when chase is { IsOpen: true } => $"{Chases(quote)} The next one is booked in your tasks for {TasksView.Day(chase.Due ?? Today)}.",
            _ => $"{Chases(quote)} The next chase is booked as soon as this page is open.",
        };

        OpenChaseButton.Visibility = chase is { IsOpen: true } && _openTask is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenChaseButton.Tag = chase?.Id;
    }

    private static string Chases(Quote quote) => quote.Chased switch
    {
        0 => "Not chased yet.",
        1 => "Chased once.",
        _ => $"Chased {quote.Chased} times.",
    };

    private void ShowNotes(Quote quote)
    {
        NoteList.Children.Clear();

        foreach (var note in quote.Notes.Reverse())
        {
            var who = note.By is { Length: > 0 } by ? $" · {by}" : string.Empty;
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            item.Children.Add(new TextBlock
            {
                Text = note.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = note.IsFromApp ? Brand.Muted : Brand.Brush("Body"),
                FontSize = 12.5,
            });
            item.Children.Add(new TextBlock
            {
                Text = $"{TasksView.Day(DateOnly.FromDateTime(note.At.LocalDateTime))}, {TasksView.Clock(TimeOnly.FromDateTime(note.At.LocalDateTime))}{who}",
                FontSize = 11,
                Foreground = Brand.Faint,
            });
            NoteList.Children.Add(item);
        }
    }

    private void ShowEmails(Quote quote)
    {
        EmailList.Children.Clear();
        EmailSection.Visibility = quote.AllEmails.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var email in quote.AllEmails)
        {
            var button = new Button
            {
                Content = $"{email.Subject}{(email.From is { Length: > 0 } from ? $" — {from}" : string.Empty)}",
                Style = (Style)FindResource("Quiet"),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MaxWidth = 360,
            };
            button.Click += (_, _) => new EmailWindow(email, Window.GetWindow(this)).Show();
            EmailList.Children.Add(button);
        }
    }

    private void OnDeselect(object sender, RoutedEventArgs e)
    {
        SavePendingNote();
        _selected = null;
        Refresh();
    }

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
        if (e.Key == Key.Escape) { _filling = true; ShowDetail(); _filling = false; }
    }

    private void OnFieldCommit(object sender, RoutedEventArgs e) => Commit();

    /// <summary>Writes the panel's boxes back to the quote, if any of them changed.</summary>
    private void Commit()
    {
        if (_filling || Chosen() is not { } quote) return;

        var customer = CustomerBox.Text.Trim();
        if (customer.Length == 0) { CustomerBox.Text = quote.Customer; return; }

        var cents = Money(ValueBox.Text) ?? quote.AmountCents;

        var edited = quote with
        {
            Customer = customer,
            What = WhatBox.Text.Trim(),
            AmountCents = cents,
            Reference = ReferenceBox.Text,
            Contact = ContactBox.Text,
        };

        if (edited.Customer == quote.Customer && edited.What == quote.What
            && edited.AmountCents == quote.AmountCents
            && (edited.Reference ?? string.Empty).Trim() == (quote.Reference ?? string.Empty)
            && (edited.Contact ?? string.Empty).Trim() == (quote.Contact ?? string.Empty))
        {
            return;
        }

        _store.Update(edited);
        Refresh();
    }

    /// <summary>What a typed value means, in cents: "4200", "$4,200", "4.2k".</summary>
    private static long? Money(string text) =>
        QuoteInput.Parse($"x ${text.Trim().TrimStart('$')} y", Today) is { AmountCents: > 0 } parsed
            ? parsed.AmountCents
            : null;

    private void OnWon(object sender, RoutedEventArgs e) => Decide(_selected, QuoteStatus.Won);

    private void OnLost(object sender, RoutedEventArgs e) => Decide(_selected, QuoteStatus.Lost);

    private void Decide(string? id, QuoteStatus status)
    {
        if (id is null || _store.Find(id) is not { } quote) return;

        _store.Decide(id, status, Today);
        _store.AddNote(id, status == QuoteStatus.Won
            ? $"Won — {QuotePlan.Money(quote.Amount)}."
            : "Lost.", TaskNote.App);

        _selected = id;
        Refresh();
    }

    private void OnReopen(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } id) return;
        _store.Reopen(id);
        _store.AddNote(id, "Open again.", TaskNote.App);
        Refresh();
    }

    private void OnOpenChase(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) _openTask?.Invoke(id);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Chosen() is not { } quote) return;

        // Asked once, in the button itself: a quote carries its notes and its history.
        if (DeleteButton.Tag as string != quote.Id)
        {
            DeleteButton.Tag = quote.Id;
            DeleteButton.Content = "Delete it — press again";
            return;
        }

        _store.Delete(quote.Id);
        _selected = null;
        Refresh();
    }

    // ---- notes ----

    private void OnNoteTyped(object sender, TextChangedEventArgs e) =>
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            SavePendingNote();
            Refresh();
        }
    }

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        SavePendingNote();
        Refresh();
    }

    /// <summary>Anything typed in the note box belongs to the quote it was typed on.</summary>
    private void SavePendingNote()
    {
        if (Detail.Tag is not string id || NoteBox.Text.Trim().Length == 0) return;

        _store.AddNote(id, NoteBox.Text, _settings().NoteAuthor);
        NoteBox.Clear();
    }

    // ---- adding one ----

    private void OnAddTyped(object sender, TextChangedEventArgs e)
    {
        AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var typed = AddBox.Text.Trim();
        if (typed.Length == 0)
        {
            AddPreview.Visibility = Visibility.Collapsed;
            return;
        }

        var parsed = QuoteInput.Parse(typed, Today);
        AddPreview.Visibility = Visibility.Visible;
        AddPreview.Text = parsed.IsUsable
            ? $"{parsed.Customer} · {QuotePlan.Money(parsed.AmountCents / 100m)}"
              + (parsed.What.Length > 0 ? $" · {parsed.What}" : string.Empty)
              + $" · sent {TasksView.Day(parsed.Sent ?? Today)}  —  Enter to add"
            : parsed.Customer.Length == 0
                ? "Start with the customer"
                : "Add what it is worth — e.g. $4,200";
    }

    private void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { AddBox.Clear(); _pendingEmail = null; EmailZone.Rest(); return; }
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        var parsed = QuoteInput.Parse(AddBox.Text, Today);
        if (!parsed.IsUsable) return;

        var quote = Add(parsed);
        AddBox.Clear();
        _selected = quote.Id;
        Refresh();
    }

    /// <summary>Makes the quote, with any dropped email attached to it.</summary>
    private Quote Add(ParsedQuote parsed)
    {
        var quote = _store.Add(
            parsed.Customer,
            parsed.What,
            parsed.AmountCents,
            parsed.Sent ?? Today,
            _settings().QuoteCadence,
            parsed.Reference);

        if (_pendingEmail is { } email)
        {
            _store.AddEmail(quote.Id, email.ToTaskEmail(DateTimeOffset.Now));
            _store.AddNote(quote.Id, $"Started from the email “{email.Subject}”.", TaskNote.App);
            _pendingEmail = null;
            EmailZone.Rest();
        }

        return quote;
    }

    // ---- an email dragged in ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!EmailDrop.CanTake(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        EmailZone.Ready("Drop to start a quote from this email");
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement page && DropZone.StillOver(page, e)) EmailZone.MaybeLeft();
        else EmailZone.Rest();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;

        EmailZone.Reading();
        var emails = await EmailDrop.ReadAsync(e.Data);
        EmailZone.Rest();

        if (emails.Count == 0)
        {
            Notice("That couldn’t be read. Type the quote in instead — the email can be dragged onto a task.", null, null);
            return;
        }

        // The email is held until the line is completed: a quote needs a value, and only the
        // person who sent it knows that. The customer and the job come from the message.
        var email = emails[0];
        _pendingEmail = email;

        AddBox.Text = $"{Customer(email)} $";
        AddBox.CaretIndex = AddBox.Text.Length;
        AddBox.Focus();

        EmailZone.Ready($"Add the value, then Enter — “{email.Subject}”");
        Notice($"Starting a quote from “{email.Subject}”. Type what it is worth, then Enter.", "Forget it", () =>
        {
            _pendingEmail = null;
            AddBox.Clear();
            EmailZone.Rest();
        });
    }

    /// <summary>Who a sent quote went to: the recipient if the message says, else the sender.</summary>
    private static string Customer(Teezy.Core.Tasks.DroppedEmail email) =>
        email.To is { Length: > 0 } to ? Name(to) : email.From is { Length: > 0 } from ? Name(from) : string.Empty;

    /// <summary>"Priya Nair &lt;priya@hunter.com.au&gt;" → "Hunter" is too clever; the name is enough.</summary>
    private static string Name(string address)
    {
        var name = address.Split('<')[0].Trim().Trim('"');
        return name.Length > 0 ? name : address.Trim();
    }

    // ---- the CRM import ----

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import quotes",
            Filter = "Spreadsheet export (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ImportPlan plan;
        try
        {
            plan = QuoteImport.Read(File.ReadAllText(dialog.FileName), Today);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Notice($"That file could not be read: {problem.Message}", null, null);
            return;
        }

        if (plan.IsEmpty)
        {
            Notice(plan.Skipped.FirstOrDefault() ?? "Nothing in that file looked like a quote.", null, null);
            return;
        }

        var columns = string.Join(", ", plan.Columns.Select(c => $"{c.Key} from “{c.Value}”"));
        var confirm = MessageBox.Show(
            Window.GetWindow(this)!,
            $"{plan.Quotes.Count} quotes found in {Path.GetFileName(dialog.FileName)}.\n\nRead as: {columns}."
            + (plan.Skipped.Count > 0 ? $"\n\n{plan.Skipped.Count} lines skipped:\n{string.Join("\n", plan.Skipped.Take(5))}" : string.Empty)
            + "\n\nImport them? Quotes already here with the same reference are brought up to date rather than duplicated.",
            "TeezyFlow",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (added, updated) = QuoteImport.Apply(_store, plan, Today);
        Notice($"Imported {added} new {(added == 1 ? "quote" : "quotes")}"
               + (updated > 0 ? $" and updated {updated}." : "."), null, null);
        Refresh();
    }

    // ---- the notice bar ----

    private void Notice(string text, string? action, Action? onAction)
    {
        NoticeText.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
        _noticeAction = onAction;
        NoticeAction.Content = action ?? string.Empty;
        NoticeAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        _noticeAction?.Invoke();
        _noticeAction = null;
        NoticeBar.Visibility = Visibility.Collapsed;
    }
}
