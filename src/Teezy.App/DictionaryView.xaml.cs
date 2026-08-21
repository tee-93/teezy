using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Teezy.Core.Dictionary;

namespace Teezy.App;

/// <summary>One dictionary entry, shaped for display.</summary>
/// <remarks>
/// A flat snapshot rebuilt on every change rather than an observable model. The list is
/// small, edits are individually cheap, and rebuilding removes a whole class of bug where
/// the view and the file disagree about what is in the dictionary.
/// </remarks>
public sealed record DictionaryRow(DictionaryEntry Entry)
{
    public bool IsEnabled => Entry.IsEnabled;
    public string Hear => Entry.Hear;
    public string Write => Entry.Write;

    public Visibility HearVisibility =>
        Entry.Kind == EntryKind.Correction ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TermTagVisibility =>
        Entry.Kind == EntryKind.Term ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Disabled entries stay legible but visibly inert.</summary>
    public double DimmedOpacity => Entry.IsEnabled ? 1.0 : 0.45;

    public string Warning =>
        string.Join(" ", DictionaryWarning.Check(Entry).Select(w => w.Message));

    public Visibility WarningVisibility =>
        Warning.Length > 0 && Entry.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Edits the personal dictionary in the app.</summary>
/// <remarks>
/// The file remains plain text and hand-editable — this is a nicer front end for it, not a
/// replacement format. "Open as text file" is kept for bulk edits, which a row-at-a-time UI
/// is genuinely worse at.
/// </remarks>
public partial class DictionaryView : UserControl
{
    private readonly DictionaryStore _store;

    /// <summary>The view's own snapshot of the entries.</summary>
    /// <remarks>
    /// Not <c>_store.Entries</c>. The app watches the dictionary file and reloads the store on
    /// a background thread whenever it changes — including changes this view just made — and
    /// <c>Parse</c> assigns a fresh <c>Id</c> to every entry each time. Editing against the
    /// store's list would mean an edit could silently match nothing and do nothing.
    /// </remarks>
    private List<DictionaryEntry> _working = [];

    public DictionaryView(DictionaryStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
        UpdateDraftState();
    }

    public void Refresh()
    {
        _store.Reload();
        _working = [.. _store.Entries];
        Render();
    }

    private void Render()
    {
        Entries.ItemsSource = _working.Select(e => new DictionaryRow(e)).ToList();

        var empty = _working.Count == 0;
        ListCard.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        var corrections = _working.Count(e => e.Kind == EntryKind.Correction);
        var terms = _working.Count - corrections;
        CountLabel.Text = empty
            ? string.Empty
            : $"{corrections} CORRECTION{(corrections == 1 ? "" : "S")} · {terms} HINT{(terms == 1 ? "" : "S")}";
    }

    /// <summary>Writes the working list to disk and re-renders from it.</summary>
    private void Commit()
    {
        _store.Save(_working);
        Render();
    }

    // ---- Adding ----

    private bool IsCorrection => KindCorrection.IsChecked == true;

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the other controls exist.
        if (HearPanel is null) return;

        var correction = IsCorrection;
        HearPanel.Visibility = correction ? Visibility.Visible : Visibility.Collapsed;
        Arrow.Visibility = correction ? Visibility.Visible : Visibility.Collapsed;
        WriteLabel.Text = correction ? "WRITE" : "THE WORD";
        UpdateDraftState();
    }

    private void OnDraftChanged(object sender, TextChangedEventArgs e) => UpdateDraftState();

    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AddButton.IsEnabled) OnAdd(sender, e);
    }

    /// <summary>Enables Add, and warns about the entry before it is committed.</summary>
    /// <remarks>
    /// Warning on the draft rather than only after adding is the point: a correction rewrites
    /// text silently and after the fact, so a bad rule is hard to notice later — the
    /// transcript is simply wrong in a plausible way.
    /// </remarks>
    private void UpdateDraftState()
    {
        if (AddButton is null) return;

        var write = WriteBox.Text.Trim();
        var hear = HearBox.Text.Trim();

        AddButton.IsEnabled = write.Length > 0 && (!IsCorrection || hear.Length > 0);

        var warnings = AddButton.IsEnabled
            ? DictionaryWarning.Check(Draft())
            : [];

        DraftWarningText.Text = string.Join(" ", warnings.Select(w => w.Message));
        DraftWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private DictionaryEntry Draft() => IsCorrection
        ? DictionaryEntry.Correction(HearBox.Text.Trim(), WriteBox.Text.Trim())
        : DictionaryEntry.Term(WriteBox.Text.Trim());

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!AddButton.IsEnabled) return;

        var entry = Draft();

        // Replace rather than duplicate. A second rule with the same trigger could never
        // fire — the first match already rewrote the text — so it would sit in the list
        // looking active and doing nothing.
        var existing = _working.FirstOrDefault(x =>
            x.Kind == entry.Kind &&
            string.Equals(x.Hear, entry.Hear, StringComparison.OrdinalIgnoreCase) &&
            (entry.Kind == EntryKind.Correction ||
             string.Equals(x.Write, entry.Write, StringComparison.OrdinalIgnoreCase)));

        if (existing is not null) _working.Remove(existing);
        _working.Add(entry);
        Commit();

        HearBox.Clear();
        WriteBox.Clear();
        (IsCorrection ? HearBox : WriteBox).Focus();
        UpdateDraftState();
    }

    // ---- Editing ----

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.Tag is not DictionaryRow row) return;

        var index = _working.FindIndex(x => x.Id == row.Entry.Id);
        if (index < 0) return;

        _working[index] = _working[index] with { IsEnabled = !_working[index].IsEnabled };
        Commit();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DictionaryRow row) return;

        _working.RemoveAll(x => x.Id == row.Entry.Id);
        Commit();
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.Path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("No app is associated with .txt files.", "Teezy",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
