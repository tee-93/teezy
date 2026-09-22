using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Teezy.Core;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>The focus list: a small card of pinned tasks that stays on top, for working through calls.</summary>
/// <remarks>
/// <para>
/// A widget, not a window: no title bar, no taskbar button, always on top, dragged by its
/// header and resized from any edge. Where it was put is remembered on this computer, and so is
/// whether it was open, so it is back in its corner after a restart.
/// </para>
/// <para>
/// It holds the tasks pinned on the Tasks page, and anything typed into it (pinned as it is
/// added). Ticking one closes it. Clicking one opens a notes box: what is written there is
/// saved to that task's notes, under the name in Settings ▸ Tasks — so a call's notes end up on
/// the task, not in a scratch pad. A note is saved as one when Save or Ctrl+Enter is pressed,
/// another task is opened, or the card is closed — not each time the card loses focus, which
/// on a call happens every time the CRM is clicked.
/// </para>
/// </remarks>
public sealed class FocusWindow : Window
{
    private readonly TaskStore _tasks;
    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _saveSettings;
    private readonly Action<string> _openTask;
    private readonly Func<string?, string?> _matchCategory;

    private readonly StackPanel _rows = new();
    private readonly TextBlock _count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
    private readonly TextBox _add = new();
    private readonly Border _undo = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _undoText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>Notes typed but not yet saved, by task.</summary>
    private readonly Dictionary<string, string> _drafts = [];
    private string? _open;
    private string? _closed;
    private TextBox? _noteBox;
    private string? _noteFor;
    private bool _quitting;

    public FocusWindow(TaskStore tasks, Func<TeezySettings> settings, Action<TeezySettings> saveSettings,
        Action<string> openTask, Func<string?, string?> matchCategory)
    {
        _tasks = tasks;
        _settings = settings;
        _saveSettings = saveSettings;
        _openTask = openTask;
        _matchCategory = matchCategory;

        Title = "TeezyFlow — focus";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        MinWidth = 240;
        MinHeight = 160;
        Background = Brand.Brush("Card");
        BorderBrush = Brand.Brush("Hairline");
        BorderThickness = new Thickness(1);
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;

        // No caption: the header is the handle. Edges still resize.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        SourceInitialized += (_, _) => RoundCorners();

        Place();
        Content = Build();
        Refresh();

        _tasks.Changed += OnTasksChanged;
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); _undo.Visibility = Visibility.Collapsed; _closed = null; };
        _placeTimer.Tick += (_, _) => { _placeTimer.Stop(); Remember(open: true); };
        LocationChanged += (_, _) => { _placeTimer.Stop(); _placeTimer.Start(); };
        SizeChanged += (_, _) => { _placeTimer.Stop(); _placeTimer.Start(); };
        Closed += (_, _) =>
        {
            SaveDraft();
            _tasks.Changed -= OnTasksChanged;
            _placeTimer.Stop();
            Remember(open: _quitting);
        };
    }

    /// <summary>TeezyFlow is quitting: the card closes, but is remembered as open for next time.</summary>
    public void Quitting()
    {
        _quitting = true;
        Close();
    }

    /// <summary>Puts the card back where it was, or in the bottom-right corner the first time.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        if (_settings().FocusBounds is [var left, var top, var width, var height]
            && width >= MinWidth && height >= MinHeight
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40
            && left + width > SystemParameters.VirtualScreenLeft + 60
            && top >= SystemParameters.VirtualScreenTop - 10)
        {
            (Left, Top, Width, Height) = (left, top, width, height);
            return;
        }

        Width = 320;
        Height = 440;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
    }

    private void Remember(bool open)
    {
        var bounds = new[] { Math.Round(Left), Math.Round(Top), Math.Round(ActualWidth), Math.Round(ActualHeight) };
        var settings = _settings();
        if (settings.FocusOpen == open && settings.FocusBounds is { } was && was.SequenceEqual(bounds)) return;
        _saveSettings(settings with { FocusOpen = open, FocusBounds = bounds });
    }

    // ============================== layout ==============================

    private Grid Build()
    {
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: the handle to move it by.
        var header = new Grid { Background = Brand.Brush("Raised") };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Cursor = Cursors.SizeAll;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Within<Button>(source)) return;
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var mark = new Image { Source = (ImageSource)FindResource("MarkImage"), Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "Focus", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brand.Ink, VerticalAlignment = VerticalAlignment.Center };
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(title);
        titles.Children.Add(_count);
        _count.Foreground = Brand.Muted;

        var open = HeaderButton("Tasks", "Open the Tasks page");
        open.Click += (_, _) => _openTask(string.Empty);
        var close = HeaderButton("✕", "Close the focus list (Esc)");
        close.Click += (_, _) => Close();

        Grid.SetColumn(titles, 1);
        Grid.SetColumn(open, 3);
        Grid.SetColumn(close, 4);
        header.Children.Add(mark);
        header.Children.Add(titles);
        header.Children.Add(open);
        header.Children.Add(close);
        page.Children.Add(new Border
        {
            Child = header,
            Height = 38,
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        });

        // Quick add: pinned as it is made, with the same "fri 2pm #Quotes" the Tasks page takes.
        var hint = new TextBlock { Text = "Add to focus — Enter", Style = (Style)FindResource("Hint"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _add.Style = (Style)FindResource("BareText");
        _add.TextChanged += (_, _) => hint.Visibility = _add.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _add.KeyDown += OnAddKey;
        var addGrid = new Grid();
        addGrid.Children.Add(hint);
        addGrid.Children.Add(_add);
        var addBox = new Border { Style = (Style)FindResource("FieldBox"), Child = addGrid, Margin = new Thickness(10, 10, 10, 6) };
        Grid.SetRow(addBox, 1);
        page.Children.Add(addBox);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows, Padding = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(scroll, 2);
        page.Children.Add(scroll);

        // Undo, after a tick: a slip on a call list should cost one click.
        var undoButton = new Button { Content = "Undo", Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 2, 8, 2) };
        undoButton.Click += (_, _) =>
        {
            if (_closed is { } id) _tasks.Reopen(id);
            _undoTimer.Stop();
            _undo.Visibility = Visibility.Collapsed;
            _closed = null;
        };
        var undoGrid = new Grid();
        undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _undoText.Foreground = Brand.Brush("Body");
        Grid.SetColumn(undoButton, 1);
        undoGrid.Children.Add(_undoText);
        undoGrid.Children.Add(undoButton);
        _undo.Child = undoGrid;
        _undo.Padding = new Thickness(12, 4, 6, 4);
        _undo.BorderBrush = Brand.Brush("Hairline");
        _undo.BorderThickness = new Thickness(0, 1, 0, 0);
        _undo.Background = Brand.Brush("Raised");
        Grid.SetRow(_undo, 3);
        page.Children.Add(_undo);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { Text.Length: > 0 }) { e.Handled = true; Close(); }
        };

        return page;
    }

    private Button HeaderButton(string text, string tip) => new()
    {
        Content = text,
        ToolTip = tip,
        Style = (Style)FindResource("Quiet"),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Arrow,
    };

    private void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _add.Text.Length > 0) { _add.Clear(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var parsed = TaskInput.Parse(_add.Text, DateOnly.FromDateTime(DateTime.Today));
        if (parsed.Title.Length == 0) return;

        var remind = parsed.DueTime is { } time && parsed.Due is { } due ? TaskPlan.At(due, time) : (DateTimeOffset?)null;
        _tasks.Add(parsed.Title, _matchCategory(parsed.Category), due: parsed.Due, dueTime: parsed.DueTime,
            remind: remind, pinned: true);
        _add.Clear();
    }

    // ============================== the list ==============================

    private void OnTasksChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        // Typing in a note is carried across the rebuild, cursor and all.
        var typing = _noteBox is { IsKeyboardFocused: true } box ? box.CaretIndex : (int?)null;
        if (_noteBox is not null && _noteFor is { } editing) _drafts[editing] = _noteBox.Text;

        var focus = TaskPlan.Focus(_tasks.Visible);
        if (_open is { } id && focus.All(t => t.Id != id)) { _open = null; typing = null; }

        _count.Text = focus.Count == 0 ? string.Empty : focus.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _rows.Children.Clear();
        _noteBox = null;
        _noteFor = null;

        if (focus.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "Nothing on your focus list.\nAdd a task above, or pin one from the Tasks page.",
                Style = (Style)FindResource("Hint"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(16, 24, 16, 0),
            });
            return;
        }

        for (var i = 0; i < focus.Count; i++) _rows.Children.Add(Row(focus[i], first: i == 0));

        if (typing is { } caret && _noteBox is { } again)
        {
            again.Focus();
            again.CaretIndex = Math.Min(caret, again.Text.Length);
        }
    }

    private Border Row(TaskItem task, bool first)
    {
        var isOpen = task.Id == _open;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var tick = new CheckBox { Style = (Style)FindResource("Tick"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0), ToolTip = "Done — closes the task" };
        tick.Checked += (_, _) =>
        {
            SaveDraft(task.Id);
            _closed = task.Id;
            _undoText.Text = $"Closed “{task.Title}”";
            _undo.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            _tasks.Close(task.Id);
        };

        var title = new TextBlock
        {
            Text = task.Title,
            Foreground = Brand.Ink,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = isOpen ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var text = new StackPanel();
        text.Children.Add(title);
        if (When(task, today) is { } label)
        {
            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = task.Due is { } d && d < today ? Brand.Brush("CautionBorder") : Brand.Muted,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        else if (!isOpen && task.Notes.Count(n => !n.IsFromApp) is > 0 and var notes)
        {
            text.Children.Add(new TextBlock { Text = notes == 1 ? "1 note" : $"{notes} notes", FontSize = 11.5, Foreground = Brand.Muted, Margin = new Thickness(0, 2, 0, 0) });
        }

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        line.Children.Add(tick);
        line.Children.Add(text);

        var stack = new StackPanel();
        stack.Children.Add(line);
        if (isOpen) stack.Children.Add(Notes(task));

        var row = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = Brand.Brush("Hairline"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = isOpen ? Brand.Brush("Selected") : Brushes.Transparent,
            Child = stack,
        };

        // The top line opens and folds the notes; clicks inside the notes stay there.
        line.Cursor = Cursors.Hand;
        line.Background = Brushes.Transparent;
        if (!isOpen)
        {
            row.MouseEnter += (_, _) => row.Background = Brand.Brush("Raised");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        line.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Within<CheckBox>(source)) return;
            SaveDraft();
            _open = isOpen ? null : task.Id;
            Refresh();
            if (_open is not null) _noteBox?.Focus();
        };

        return row;
    }

    /// <summary>The open task's recent notes and a box for the call's notes.</summary>
    private StackPanel Notes(TaskItem task)
    {
        var panel = new StackPanel { Margin = new Thickness(28, 8, 0, 0) };

        foreach (var note in task.Notes.Where(n => !n.IsFromApp).TakeLast(3))
        {
            var who = note.By is { Length: > 0 } by ? $" · {by}" : string.Empty;
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            item.Children.Add(new TextBlock { Text = note.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brand.Brush("Body"), FontSize = 12.5 });
            item.Children.Add(new TextBlock { Text = $"{Stamp(note.At)}{who}", FontSize = 11, Foreground = Brand.Faint });
            panel.Children.Add(item);
        }

        var placeholder = new TextBlock
        {
            Text = "Notes from the call — Ctrl+Enter saves",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        var box = new TextBox
        {
            Style = (Style)FindResource("BareText"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0, 5, 0, 5),
            Text = _drafts.GetValueOrDefault(task.Id, string.Empty),
        };
        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var save = new Button { Content = "Save note", Style = (Style)FindResource("Primary"), Padding = new Thickness(12, 3, 12, 3), IsEnabled = box.Text.Trim().Length > 0 };
        box.TextChanged += (_, _) =>
        {
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = box.Text.Trim().Length > 0;
            _drafts[task.Id] = box.Text;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; SaveDraft(task.Id); }
        };
        save.Click += (_, _) => SaveDraft(task.Id);

        var grid = new Grid();
        grid.Children.Add(placeholder);
        grid.Children.Add(box);
        panel.Children.Add(new Border
        {
            Style = (Style)FindResource("FieldBox"),
            Height = double.NaN,
            Padding = new Thickness(8, 0, 8, 0),
            Child = grid,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        buttons.Children.Add(save);
        var details = new Button { Content = "Open", Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Open this task on the Tasks page" };
        details.Click += (_, _) => { SaveDraft(task.Id); _openTask(task.Id); };
        var unpin = new Button { Content = "Unpin", Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Take it off the focus list; the task stays open" };
        unpin.Click += (_, _) => { SaveDraft(task.Id); _open = null; _tasks.Pin(task.Id, false); };
        buttons.Children.Add(details);
        buttons.Children.Add(unpin);
        panel.Children.Add(buttons);

        _noteBox = box;
        _noteFor = task.Id;
        return panel;
    }

    /// <summary>Saves the note being written on the open task, if any.</summary>
    private void SaveDraft() { if (_noteFor is { } id) SaveDraft(id); }

    private void SaveDraft(string id)
    {
        var box = id == _noteFor ? _noteBox : null;
        if (box is not null) _drafts[id] = box.Text;
        if (!_drafts.Remove(id, out var text) || text.Trim().Length == 0) return;
        if (box is not null)
        {
            box.Clear();
            _drafts.Remove(id);
        }
        _tasks.AddNote(id, text, _settings().NoteAuthor);
    }

    /// <summary>"Late — Mon 21 Sep", "today 2:00 pm", "Fri 25 Sep", or nothing for no date.</summary>
    private static string? When(TaskItem task, DateOnly today)
    {
        if (task.Due is not { } due) return null;
        var day = TasksView.Day(due);
        var at = task.DueTime is { } time ? $" {TasksView.Clock(time)}" : string.Empty;
        return due < today ? $"Late — due {day}" : $"{char.ToUpper(day[0], System.Globalization.CultureInfo.CurrentCulture)}{day[1..]}{at}";
    }

    private static string Stamp(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        var time = TasksView.Clock(TimeOnly.FromDateTime(local));
        return local.Date == DateTime.Today ? time : $"{local:ddd d MMM}, {time}";
    }

    private static bool Within<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? at = node; at is not null;
             at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is T) return true;
        }
        return false;
    }

    // Windows 11 draws a borderless window square; ask for the usual rounded corners.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void RoundCorners()
    {
        const int CornerPreference = 33, Round = 2;
        var value = Round;
        try { _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, CornerPreference, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
