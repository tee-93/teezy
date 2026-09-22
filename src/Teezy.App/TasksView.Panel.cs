using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teezy.Core.Commands;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>The panel for the chosen task: dates, category, closing, notes, its email and the AI.</summary>
/// <remarks>
/// <para>
/// <b>Notes are a timeline</b>, newest first, each saying who wrote it and when — the record to
/// read back in a review. Notes TeezyFlow writes itself (closed, followed up, an email attached)
/// are shown quieter than yours.
/// </para>
/// <para>
/// <b>The email and the AI fold away.</b> An email is someone else's page of text; it is kept
/// beside the task, summarised in one line, and read in its own window. The AI's answer is kept
/// on the task too, and can be edited like a note before it is copied.
/// </para>
/// </remarks>
public partial class TasksView
{
    private bool _emailOpen;
    private bool _aiOpen;
    private bool _panelFilling;
    private bool _advising;

    /// <summary>Opens Settings ▸ Tasks, for the Manage categories link. Set by the window.</summary>
    internal Action? OpenTaskSettings { get; set; }

    /// <summary>Opens the focus card. Set by the window; the button is hidden without it.</summary>
    internal Action? ShowFocus
    {
        get => _showFocus;
        set
        {
            _showFocus = value;
            FocusButton.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private Action? _showFocus;

    private void OnShowFocus(object sender, RoutedEventArgs e) => _showFocus?.Invoke();

    /// <summary>Pins or unpins the task; pinning brings the focus card out, since that is where it went.</summary>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        _store.Pin(id, !task.Pinned);
        if (!task.Pinned) _showFocus?.Invoke();
    }

    private void InitPanel()
    {
        AiSection.Visibility = _advisor is not null ? Visibility.Visible : Visibility.Collapsed;

        DueField.Changed += () =>
        {
            if (_selected is not { } id || _store.Find(id) is not { } task) return;
            _store.Update(task with { Due = DueField.Date, DueTime = DueField.Time });
        };

        RemindField.Changed += () =>
        {
            if (_selected is not { } id || _store.Find(id) is not { } task) return;

            // A moved reminder is a new reminder, shown again even if the old one was.
            _store.Update(task with { Remind = RemindField.Moment, Reminded = null });
        };

        FollowUpDay.Changed += () => FollowUpThen.IsEnabled = FollowUpDay.Date is not null;

        // Leaving the page keeps what was typed.
        Unloaded += (_, _) => SavePendingNote();

        // A text box swallows the wheel even when it has nothing to scroll, which left the panel
        // stuck whenever the pointer rested on a note. The panel scrolls unless the box can.
        DetailScroll.PreviewMouseWheel += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindTextBox(source) is { } box
                && box.ExtentHeight > box.ViewportHeight + 1)
            {
                return;
            }

            DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        };
    }

    private void OnDeselect(object sender, RoutedEventArgs e)
    {
        SavePendingNote();
        _selected = null;
        Refresh();
    }

    private bool DetailHasFocus() =>
        Detail.Visibility == Visibility.Visible && Detail.IsKeyboardFocusWithin;

    /// <param name="fields">Whether to refill the text boxes, which is skipped while one is being typed in.</param>
    private void ShowDetail(bool fields)
    {
        var task = _selected is { } id ? _store.Find(id) : null;
        if (task is null)
        {
            _selected = null;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        var changed = Detail.Tag as string != task.Id;

        // Before the panel switches: an unsaved note belongs to the task it was typed on.
        if (changed) SavePendingNote();
        Detail.Tag = task.Id;
        Detail.Visibility = Visibility.Visible;
        _panelFilling = true;

        if (changed)
        {
            NoteBox.Clear();
            SteerBox.Clear();
            PasteEmailBox.Clear();
            PasteEmailPanel.Visibility = Visibility.Collapsed;
            AiStatus.Visibility = Visibility.Collapsed;
            FollowUpDay.Set(null, null);
            FollowUpThen.IsEnabled = false;
            DeleteButton.Tag = null;
            DeleteButton.Content = "Delete task";
            DetailScroll.ScrollToTop();
        }

        if (fields || changed)
        {
            TitleBox.Text = task.Title;
            if (!AdviceBox.IsKeyboardFocused) AdviceBox.Text = task.Advice ?? string.Empty;
        }

        DueField.Set(task.Due, task.DueTime);
        RemindField.Set(task.Remind);
        PinButton.Content = task.Pinned ? "Unpin from focus" : "Pin to focus";
        PinButton.Visibility = task.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        var bucket = TaskPlan.BucketOf(task, Today);
        DetailState.Text = task.IsOpen
            ? BucketName(bucket)
            : $"CLOSED {Day(DateOnly.FromDateTime(task.Closed!.Value.LocalDateTime)).ToUpper(Display)}";
        DetailState.Foreground = task.IsOpen && bucket == TaskBucket.Overdue ? Brush("CautionBorder") : Brush("Muted");

        DetailCreated.Text = $"Created {task.Created.LocalDateTime.ToString("ddd d MMM, h:mm tt", Display)}";

        OpenActions.Visibility = task.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ReopenButton.Visibility = task.IsOpen ? Visibility.Collapsed : Visibility.Visible;

        BuildCategoryPicker(task);
        BuildFollowUpChoices();
        BuildChain(task);
        BuildNotes(task);
        BuildEmails(task);
        BuildAi(task);
        _panelFilling = false;
    }

    // ---- title and category ----

    private void OnTitleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selected is { } id && _store.Find(id) is { } task) TitleBox.Text = task.Title;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
        }
    }

    private void OnTitleCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitTitle();

    private void CommitTitle()
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { TitleBox.Text = task.Title; return; }
        if (title != task.Title) _store.Update(task with { Title = title });
    }

    private void BuildCategoryPicker(TaskItem task)
    {
        CategoryPicker.Items.Clear();
        CategoryPicker.Items.Add(new ComboBoxItem { Content = "No category", Tag = null });

        foreach (var category in Categories(_store.Visible))
        {
            CategoryPicker.Items.Add(new ComboBoxItem { Content = category, Tag = category });
        }

        CategoryPicker.SelectedIndex = Math.Max(0, CategoryPicker.Items.Cast<ComboBoxItem>().ToList()
            .FindIndex(i => string.Equals(i.Tag as string, task.Category, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnCategoryChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_panelFilling || _selected is not { } id || _store.Find(id) is not { } task) return;
        if (CategoryPicker.SelectedItem is not ComboBoxItem item) return;

        var category = item.Tag as string;
        if (!string.Equals(category, task.Category, StringComparison.Ordinal)) _store.Update(task with { Category = category });
    }

    private void OnManageCategories(object sender, RoutedEventArgs e) => OpenTaskSettings?.Invoke();

    // ---- closing ----

    private void OnFollowUpOn(object sender, RoutedEventArgs e)
    {
        if (FollowUpDay.Date is { } day) FollowUp(day);
    }

    private void BuildChain(TaskItem task)
    {
        ChainList.Children.Clear();
        var chain = TaskPlan.Chain(_store.Visible, task.Id);
        ChainSection.Visibility = chain.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (chain.Count < 2) return;

        for (var i = 0; i < chain.Count; i++)
        {
            var link = chain[i];
            var when = link.Closed is { } closed
                ? $"closed {Day(DateOnly.FromDateTime(closed.LocalDateTime))}"
                : link.Due is { } due ? $"open · due {Day(due)}" : "open";

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = link.Title,
                Foreground = link.Id == task.Id ? Brush("AccentInk") : link.IsOpen ? Brush("Ink") : Brush("Muted"),
                FontWeight = link.Id == task.Id ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock { Text = when, FontSize = 12, Foreground = Brush("Muted") });

            var row = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                BorderBrush = Brush("Hairline"),
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Background = Brushes.Transparent,
                Cursor = link.Id == task.Id ? Cursors.Arrow : Cursors.Hand,
                Child = text,
            };
            if (link.Id != task.Id)
            {
                row.MouseEnter += (_, _) => row.Background = Brush("Raised");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => Select(link.Id);
            }

            ChainList.Children.Add(row);
        }
    }

    // ---- notes: the timeline ----

    private void OnNoteTyped(object sender, TextChangedEventArgs e)
    {
        var empty = NoteBox.Text.Trim().Length == 0;
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddNoteButton.IsEnabled = !empty;
    }

    private void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            AddNote();
        }
    }

    private void OnAddNote(object sender, RoutedEventArgs e) => AddNote();

    /// <summary>Save: the note, and anything half-edited in the panel with it.</summary>
    private void AddNote()
    {
        if (_selected is not { } id) return;
        CommitTitle();
        OnAdviceEdited(this, null!);
        if (NoteBox.Text.Trim().Length == 0) return;

        _store.AddNote(id, NoteBox.Text, _settings().NoteAuthor);
        NoteBox.Clear();
        Refresh();
    }

    /// <summary>
    /// A note typed but not saved is saved anyway when the panel moves on — to another task,
    /// closed, or the page left — so nothing written is ever lost.
    /// </summary>
    private void SavePendingNote()
    {
        if (Detail.Tag is not string id || NoteBox.Text.Trim().Length == 0) return;
        if (_store.Find(id) is null) return;
        _store.AddNote(id, NoteBox.Text, _settings().NoteAuthor);
        NoteBox.Clear();
    }

    private void BuildNotes(TaskItem task)
    {
        NotesList.Children.Clear();
        NotesLabel.Text = task.Notes.Count == 0 ? "NOTES" : $"NOTES  {task.Notes.Count}";

        if (task.Notes.Count == 0)
        {
            NotesList.Children.Add(new TextBlock { Text = "No notes yet.", Style = Styled("FormHint"), Margin = new Thickness(0) });
            return;
        }

        // Newest first, on a line down the left, so the latest is where the eye lands.
        var notes = task.Notes.Reverse().ToList();
        for (var i = 0; i < notes.Count; i++) NotesList.Children.Add(NoteEntry(notes[i], last: i == notes.Count - 1));
    }

    private Grid NoteEntry(TaskNote note, bool last)
    {
        var fromApp = note.IsFromApp;
        var who = note.By ?? "Note";

        // The marker: your initial in a circle, or TeezyFlow's bars for its own notes.
        FrameworkElement marker = fromApp
            ? new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush("Raised"),
                Child = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("MarkGeometry"), Fill = Brush("Muted"), Stretch = Stretch.Uniform,
                    Width = 10, Height = 10,
                },
            }
            : new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush("AccentSoft"),
                Child = new TextBlock
                {
                    Text = who[..1].ToUpper(Display), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("AccentInk"), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

        var rail = new Border
        {
            Width = 1, Background = Brush("Hairline"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0), Visibility = last ? Visibility.Collapsed : Visibility.Visible,
        };

        var left = new Grid { Width = 22, Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(rail);
        left.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Top, Child = marker });

        var head = new TextBlock { FontSize = 12, Margin = new Thickness(0, 3, 0, 2) };
        head.Inlines.Add(new System.Windows.Documents.Run(who) { Foreground = fromApp ? Brush("Muted") : Brush("Ink"), FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.Run("  " + When(note.At)) { Foreground = Brush("Faint") });

        // A read-only box so any part of a note can be selected and copied.
        var text = new TextBox
        {
            Text = note.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = fromApp ? Brush("Muted") : Brush("Body"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = fromApp ? 12 : 13,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, 0, 12),
        };

        var right = new StackPanel();
        right.Children.Add(head);
        right.Children.Add(text);

        var entry = new Grid();
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        entry.Children.Add(left);
        entry.Children.Add(right);
        return entry;
    }

    /// <summary>"Today 11:26 am", "Yesterday 4:10 pm", "Mon 21 Sep, 9:00 am".</summary>
    private static string When(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        var day = DateOnly.FromDateTime(local);
        var clock = TasksView.Clock(TimeOnly.FromDateTime(local));
        return (Today.DayNumber - day.DayNumber) switch
        {
            0 => $"Today {clock}",
            1 => $"Yesterday {clock}",
            _ => $"{local.ToString(day.Year == Today.Year ? "ddd d MMM" : "ddd d MMM yyyy", Display)}, {clock}",
        };
    }

    // ---- the email ----

    private void BuildEmails(TaskItem task)
    {
        var emails = task.AllEmails;
        EmailHeader.Text = emails.Count switch
        {
            0 => "EMAIL",
            1 => "EMAIL",
            _ => $"EMAILS  {emails.Count}",
        };
        EmailSummary.Text = emails.Count == 0
            ? "None yet — drag one from Outlook onto the task, or paste one"
            : Summary(emails[^1]);

        EmailToggle.IsChecked = _emailOpen;
        EmailBody.Visibility = _emailOpen ? Visibility.Visible : Visibility.Collapsed;

        EmailList.Children.Clear();
        foreach (var email in emails.Reverse()) EmailList.Children.Add(EmailCard(task, email));
    }

    private static string Summary(TaskEmail email) =>
        email.From is { Length: > 0 } from ? $"{Sender(from)} — {email.Subject}" : email.Subject;

    /// <summary>"Priya Nair" from "Priya Nair &lt;priya@example.com&gt;".</summary>
    private static string Sender(string from)
    {
        var at = from.IndexOf('<', StringComparison.Ordinal);
        var name = at > 0 ? from[..at].Trim().Trim('"') : from;
        return name.Length > 0 ? name : from;
    }

    private Border EmailCard(TaskItem task, TaskEmail email)
    {
        var when = email.Received ?? email.Added;
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = email.Subject, Foreground = Brush("Ink"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock
        {
            Text = email.From is { Length: > 0 } from ? $"{Sender(from)} · {When(when)}" : When(when),
            FontSize = 12, Foreground = Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var preview = email.Body.Replace('\n', ' ').Trim();
        info.Children.Add(new TextBlock
        {
            Text = preview.Length > 160 ? preview[..160] + "…" : preview,
            FontSize = 12, Foreground = Brush("Body"), TextWrapping = TextWrapping.Wrap, MaxHeight = 36,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var open = new Button { Content = "Open", Style = Styled("Secondary") };
        open.Click += (_, _) => new EmailWindow(email, Window.GetWindow(this)).ShowDialog();

        var remove = new Button { Content = "Remove", Style = Styled("Quiet"), Margin = new Thickness(4, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            if (remove.Tag is not "armed") { remove.Tag = "armed"; remove.Content = "Remove?"; return; }
            if (_store.Find(task.Id) is { } current)
            {
                _store.Update(current with { Emails = [.. current.AllEmails.Where(m => m != email)] });
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(remove);
        info.Children.Add(buttons);

        var card = new Border
        {
            Background = Brush("Sunken"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand, Child = info,
        };

        // A double click reads it, as in Outlook.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) new EmailWindow(email, Window.GetWindow(this)).ShowDialog();
        };
        return card;
    }

    private void OnEmailToggled(object sender, RoutedEventArgs e)
    {
        if (_panelFilling) return;
        _emailOpen = EmailToggle.IsChecked == true;
        EmailBody.Visibility = _emailOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowPasteEmail(object sender, RoutedEventArgs e)
    {
        PasteEmailPanel.Visibility = Visibility.Visible;
        PasteEmailBox.Focus();
    }

    private void OnCancelPaste(object sender, RoutedEventArgs e)
    {
        PasteEmailBox.Clear();
        PasteEmailPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAttachPasted(object sender, RoutedEventArgs e)
    {
        var text = PasteEmailBox.Text.Trim();
        if (_selected is not { } id || text.Length == 0) return;

        // A pasted email has no headers to read; its first line names it.
        var first = text.Split('\n')[0].Trim();
        var email = new TaskEmail(DateTimeOffset.Now, first.Length > 100 ? first[..100] + "…" : first, null, null, text);
        _store.AddEmail(id, email);
        _store.AddNote(id, $"Email attached: {email.Subject}", TaskNote.App);
        PasteEmailBox.Clear();
        PasteEmailPanel.Visibility = Visibility.Collapsed;
    }

    // ---- the AI, on request ----

    private void BuildAi(TaskItem task)
    {
        if (_advisor is null) return;

        var emails = task.AllEmails;
        AiToggle.IsChecked = _aiOpen;
        AiBody.Visibility = _aiOpen ? Visibility.Visible : Visibility.Collapsed;
        AiSummary.Text = task.Advice is { Length: > 0 } advice
            ? FirstLine(advice)
            : emails.Count == 0 ? "Attach an email first" : "Suggest what to do, or draft a reply";

        // Which email it is about: the only one, or a choice when there are several.
        AiEmailPicker.Items.Clear();
        foreach (var email in emails.Reverse()) AiEmailPicker.Items.Add(new ComboBoxItem { Content = Summary(email), Tag = email });
        AiEmailPicker.Visibility = emails.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (emails.Count > 0) AiEmailPicker.SelectedIndex = 0;

        AiSource.Text = emails.Count switch
        {
            0 => "Needs an email: drag one onto the task, or paste one in the Email section above.",
            1 => $"About: {Summary(emails[0])}",
            _ => "About:",
        };

        // Not while a request is out: a refresh from elsewhere must not offer a second one.
        NextStepsButton.IsEnabled = DraftReplyButton.IsEnabled = emails.Count > 0 && !_advising;
        AdvicePanel.Visibility = task.Advice is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim().TrimStart('-', ' ');
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private void OnAiToggled(object sender, RoutedEventArgs e)
    {
        if (_panelFilling) return;
        _aiOpen = AiToggle.IsChecked == true;
        AiBody.Visibility = _aiOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSteerTyped(object sender, TextChangedEventArgs e) =>
        SteerPlaceholder.Visibility = SteerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnNextSteps(object sender, RoutedEventArgs e) => _ = AdviseAsync(AdviceKind.NextSteps);

    private void OnDraftReply(object sender, RoutedEventArgs e) => _ = AdviseAsync(AdviceKind.DraftReply);

    private async Task AdviseAsync(AdviceKind kind)
    {
        if (_advisor is null || _selected is not { } id || _store.Find(id) is not { } task) return;
        if (AiEmailPicker.SelectedItem is not ComboBoxItem { Tag: TaskEmail email }) return;

        AiStatus.Text = kind == AdviceKind.DraftReply ? "Drafting a reply…" : "Thinking about next steps…";
        AiStatus.Visibility = Visibility.Visible;
        _advising = true;
        NextStepsButton.IsEnabled = DraftReplyButton.IsEnabled = false;

        // The headers help it: who is asking, and when.
        var text = $"From: {email.From ?? "unknown"}\nSubject: {email.Subject}\n"
                   + (email.Received is { } received ? $"Received: {received.LocalDateTime.ToString("d MMMM yyyy, h:mm tt", Display)}\n" : string.Empty)
                   + "\n" + email.Body;

        string? result;
        try
        {
            result = await _advisor.AdviseAsync(kind, task, text, SteerBox.Text, DateTimeOffset.Now);
        }
        catch (AssistantUnavailableException problem)
        {
            AiStatus.Text = problem.Message;
            return;
        }
        catch (Exception problem)
        {
            // Started from a button and not awaited by anything, so nothing else would ever see
            // this; say it rather than leave "Thinking…" up for good.
            AiStatus.Text = $"Something went wrong: {problem.Message}";
            return;
        }
        finally
        {
            _advising = false;
            NextStepsButton.IsEnabled = DraftReplyButton.IsEnabled = true;
        }

        if (result is null)
        {
            AiStatus.Text = "Nothing useful came back. Try again.";
            return;
        }

        AiStatus.Visibility = Visibility.Collapsed;

        // Kept on the task, headed so it reads right when it comes back later; the panel may have
        // moved to another task while Claude was answering, so it is saved to the one it was for.
        var heading = kind == AdviceKind.DraftReply ? "Draft reply" : "Next steps";
        if (_store.Find(id) is { } current) _store.Update(current with { Advice = $"{heading}:\n{result}" });

        if (_selected == id)
        {
            AdviceBox.Text = $"{heading}:\n{result}";
            AdvicePanel.Visibility = Visibility.Visible;
            CopyAdviceButton.Content = "Copy";
            SaveAdviceButton.Content = "Save to notes";
            SaveAdviceButton.IsEnabled = true;
        }
    }

    private void OnAdviceEdited(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        var text = AdviceBox.Text.Trim();
        if (text != (task.Advice ?? string.Empty)) _store.Update(task with { Advice = text.Length == 0 ? null : text });
    }

    private void OnCopyAdvice(object sender, RoutedEventArgs e)
    {
        // Without its "Draft reply:" heading: what is copied is what gets pasted into Outlook.
        var text = AdviceBox.Text;
        var lines = text.Split('\n');
        if (lines.Length > 1 && lines[0].TrimEnd().EndsWith(':') && lines[0].Length < 20) text = string.Join('\n', lines[1..]);

        try { Clipboard.SetText(text.Trim()); CopyAdviceButton.Content = "Copied"; }
        catch (System.Runtime.InteropServices.COMException) { CopyAdviceButton.Content = "Clipboard busy — try again"; }
    }

    private void OnSaveAdvice(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } id || AdviceBox.Text.Trim().Length == 0) return;
        _store.AddNote(id, AdviceBox.Text, _settings().NoteAuthor);
        SaveAdviceButton.Content = "Saved";
        SaveAdviceButton.IsEnabled = false;
    }

    private void OnClearAdvice(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } id || _store.Find(id) is not { } task) return;
        AdviceBox.Clear();
        _store.Update(task with { Advice = null });
    }
}
