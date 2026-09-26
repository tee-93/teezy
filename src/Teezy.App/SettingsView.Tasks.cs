using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Teezy.Core;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>Settings ▸ Tasks: the category list and the name on notes.</summary>
public partial class SettingsView
{
    private TaskStore? _taskStore;

    /// <summary>Hooked up by the window, so renaming a category can rename it on the tasks too.</summary>
    internal void AttachTasks(TaskStore? tasks)
    {
        _taskStore ??= tasks;
        ShowTaskSettings();
    }

    /// <summary>Opens a section by its panel name, e.g. "TabTasks".</summary>
    internal void ShowTab(string tab)
    {
        foreach (var button in FindTabs(this))
        {
            if (button.Tag as string == tab) button.IsChecked = true;
        }

        if (tab == "TabTasks") ShowTaskSettings();
    }

    private static IEnumerable<RadioButton> FindTabs(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is RadioButton { GroupName: "SettingsTabs" } tab) yield return tab;
            foreach (var inner in FindTabs(child)) yield return inner;
        }
    }

    private void ShowTaskSettings()
    {
        var settings = _read();

        // First visit with tasks already categorised: start the list from what is in use.
        if (settings.TaskCategories.Count == 0 && _taskStore is not null)
        {
            var used = TaskPlan.Categories(_taskStore.Visible);
            if (used.Count > 0)
            {
                settings = settings with { TaskCategories = used };
                _write(settings);
            }
        }

        ShowCadence(settings);

        CategoryRows.Children.Clear();
        var list = settings.TaskCategories;
        for (var i = 0; i < list.Count; i++) CategoryRows.Children.Add(CategoryRow(list, i));
        CategoryAddRow.Style = (Style)FindResource(list.Count == 0 ? "FormRowFirst" : "FormRow");

        ShowBriefingSettings(settings);

        if (!AuthorBox.IsKeyboardFocused) AuthorBox.Text = settings.TaskAuthor ?? string.Empty;
        AuthorHint.Text = $"Shown on every note you write. Empty uses “{TeezySettings.FirstName(Environment.UserName)}”, from your Windows account.";
    }

    private Border CategoryRow(IReadOnlyList<string> list, int index)
    {
        var name = list[index];
        var count = _taskStore?.Visible.Count(t => t.IsOpen && string.Equals(t.Category, name, StringComparison.OrdinalIgnoreCase)) ?? 0;

        var label = new TextBlock { Text = name, Style = (Style)FindResource("FormLabel"), VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock
        {
            Text = count == 0 ? "No open tasks" : count == 1 ? "1 open task" : $"{count} open tasks",
            Style = (Style)FindResource("FormHint"),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(label);
        text.Children.Add(detail);

        // Renaming happens in place: the name becomes a box.
        var edit = new TextBox { Style = (Style)FindResource("BareText"), Text = name };
        var editBox = new Border { Style = (Style)FindResource("FieldBox"), Child = edit, Visibility = Visibility.Collapsed };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string content, string tip, Action run, bool enabled = true)
        {
            var button = new Button { Content = content, Style = (Style)FindResource("Quiet"), ToolTip = tip, IsEnabled = enabled, Margin = new Thickness(2, 0, 0, 0) };
            button.Click += (_, _) => run();
            buttons.Children.Add(button);
            return button;
        }

        Small("↑", "Move up", () => Move(index, -1), index > 0);
        Small("↓", "Move down", () => Move(index, 1), index < list.Count - 1);
        Small("Rename", "Rename", () =>
        {
            text.Visibility = Visibility.Collapsed;
            editBox.Visibility = Visibility.Visible;
            edit.Focus();
            edit.SelectAll();
        });
        Button? remove = null;
        remove = Small("Remove", "Remove from the list", () =>
        {
            if (remove!.Tag is not "armed") { remove.Tag = "armed"; remove.Content = "Remove?"; return; }
            var settings = _read();
            _write(settings with { TaskCategories = [.. settings.TaskCategories.Where((_, i) => i != index)] });
            ShowTaskSettings();
        });

        void Commit()
        {
            var renamed = edit.Text.Trim();
            if (renamed.Length > 0 && renamed != name) Rename(name, renamed);
            else ShowTaskSettings();
        }

        edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; ShowTaskSettings(); }
        };
        edit.LostKeyboardFocus += (_, _) => { if (editBox.Visibility == Visibility.Visible) Commit(); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(editBox);
        grid.Children.Add(buttons);

        return new Border { Style = (Style)FindResource(index == 0 ? "FormRowFirst" : "FormRow"), Padding = new Thickness(12, 8, 8, 8), Child = grid };
    }

    private void Move(int index, int by)
    {
        var list = _read().TaskCategories.ToList();
        var to = index + by;
        if (to < 0 || to >= list.Count) return;
        (list[index], list[to]) = (list[to], list[index]);
        _write(_read() with { TaskCategories = list });
        ShowTaskSettings();
    }

    /// <summary>Renames a category in the list and on every task that has it.</summary>
    private void Rename(string from, string to)
    {
        var settings = _read();
        if (settings.TaskCategories.Any(c => c.Equals(to, StringComparison.OrdinalIgnoreCase) && !c.Equals(from, StringComparison.OrdinalIgnoreCase)))
        {
            // Already there under that name: renaming merges the two.
            _write(settings with { TaskCategories = [.. settings.TaskCategories.Where(c => c != from)] });
        }
        else
        {
            _write(settings with { TaskCategories = [.. settings.TaskCategories.Select(c => c == from ? to : c)] });
        }

        if (_taskStore is not null)
        {
            foreach (var task in _taskStore.Visible.Where(t => string.Equals(t.Category, from, StringComparison.OrdinalIgnoreCase)))
            {
                _taskStore.Update(task with { Category = to });
            }
        }

        ShowTaskSettings();
    }

    private void OnNewCategoryTyped(object sender, TextChangedEventArgs e)
    {
        NewCategoryPlaceholder.Visibility = NewCategoryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var name = NewCategoryBox.Text.Trim();
        AddCategoryButton.IsEnabled = name.Length > 0
            && !_read().TaskCategories.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void OnNewCategoryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (AddCategoryButton.IsEnabled) OnAddCategory(sender, e);
    }

    private void OnAddCategory(object sender, RoutedEventArgs e)
    {
        var name = NewCategoryBox.Text.Trim().TrimStart('#');
        if (name.Length == 0) return;
        var settings = _read();
        if (settings.TaskCategories.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        _write(settings with { TaskCategories = [.. settings.TaskCategories, name] });
        NewCategoryBox.Clear();
        ShowTaskSettings();
    }

    // ---- the morning briefing ----

    /// <summary>Shows the briefing now, from Show it now. Set by the window.</summary>
    internal Action? ShowBriefing { get; set; }

    private bool _briefingFilling;

    /// <summary>How long after a quote goes out it is chased, and again after that.</summary>
    private static readonly (int[] Days, string Label)[] Cadences =
    [
        ([2, 5, 10], "2, 5 and 10 days"),
        ([3, 7, 14], "3 days, a week, a fortnight"),
        ([7, 14, 28], "A week, a fortnight, a month"),
        ([14], "A fortnight, then monthly"),
    ];

    private bool _cadenceFilling;

    private void ShowCadence(TeezySettings settings)
    {
        _cadenceFilling = true;

        if (CadencePicker.Items.Count == 0)
        {
            foreach (var (days, label) in Cadences)
            {
                CadencePicker.Items.Add(new ComboBoxItem { Content = label, Tag = days });
            }
        }

        var chosen = CadencePicker.Items.Cast<ComboBoxItem>().FirstOrDefault(
            i => i.Tag is int[] days && days.SequenceEqual(settings.QuoteCadence));

        CadencePicker.SelectedItem = chosen ?? CadencePicker.Items.Cast<ComboBoxItem>()
            .First(i => i.Tag is int[] days && days.SequenceEqual(QuotePlan.DefaultCadence));

        _cadenceFilling = false;
    }

    private void OnCadenceChanged(object sender, RoutedEventArgs e)
    {
        if (_cadenceFilling || CadencePicker.SelectedItem is not ComboBoxItem { Tag: int[] days }) return;
        _write(_read() with { QuoteCadence = days });
    }

    private void ShowBriefingSettings(TeezySettings settings)
    {
        _briefingFilling = true;
        BriefingOnBox.IsChecked = settings.BriefingOn;
        BriefingWeekendsBox.IsChecked = settings.BriefingWeekends;
        BriefingSummaryBox.IsChecked = settings.BriefingSummary;
        BriefingDetail.Visibility = settings.BriefingOn ? Visibility.Visible : Visibility.Collapsed;

        if (BriefingTimePicker.Items.Count == 0)
        {
            for (var minutes = 6 * 60; minutes <= 11 * 60; minutes += 15)
            {
                var time = new TimeOnly(minutes / 60, minutes % 60);
                BriefingTimePicker.Items.Add(new ComboBoxItem { Content = TasksView.Clock(time), Tag = time });
            }
        }

        BriefingTimePicker.SelectedItem = BriefingTimePicker.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is TimeOnly t && t == settings.BriefingTime)
            ?? BriefingTimePicker.Items.Cast<ComboBoxItem>().First(i => i.Tag is TimeOnly t && t == new TimeOnly(8, 30));
        _briefingFilling = false;
    }

    private void OnBriefingChanged(object sender, RoutedEventArgs e)
    {
        if (_briefingFilling) return;
        _write(_read() with
        {
            BriefingOn = BriefingOnBox.IsChecked == true,
            BriefingWeekends = BriefingWeekendsBox.IsChecked == true,
            BriefingSummary = BriefingSummaryBox.IsChecked == true,
        });
        ShowBriefingSettings(_read());
    }

    private void OnBriefingTimeChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_briefingFilling || BriefingTimePicker.SelectedItem is not ComboBoxItem { Tag: TimeOnly time }) return;
        if (time != _read().BriefingTime) _write(_read() with { BriefingTime = time });
    }

    private void OnShowBriefingNow(object sender, RoutedEventArgs e) => ShowBriefing?.Invoke();

    // ---- Advanced ▸ When I close the window (kept here with the other small pickers) ----

    private bool _closeActionFilling;

    private void OnCloseActionLoaded(object sender, RoutedEventArgs e)
    {
        _closeActionFilling = true;
        var current = _read().CloseAction.ToString();
        CloseActionPicker.SelectedItem = CloseActionPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == current);
        _closeActionFilling = false;
    }

    private void OnCloseActionChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_closeActionFilling || CloseActionPicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (Enum.TryParse<CloseAction>(tag, out var action) && action != _read().CloseAction)
        {
            _write(_read() with { CloseAction = action });
        }
    }

    private void OnAuthorKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitAuthor();
    }

    private void OnAuthorCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitAuthor();

    private void CommitAuthor()
    {
        var name = AuthorBox.Text.Trim();
        var settings = _read();
        var value = name.Length == 0 ? null : name;
        if (value != settings.TaskAuthor) _write(settings with { TaskAuthor = value });
    }
}
