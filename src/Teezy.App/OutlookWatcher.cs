using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Teezy.Core.Calendar;

namespace Teezy.App;

/// <summary>What Settings shows for the Outlook route.</summary>
public sealed record OutlookReadStatus(string Message, bool Problem = false);

/// <summary>
/// Reads the meetings New Outlook is showing, the way a screen reader does, and keeps a local copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route for a work calendar that allows nothing else.</b> Where the organisation blocks
/// sign-in for unapproved apps, calendar publishing, and every Microsoft 365 connector in Power
/// Automate, what is left is the calendar already on the screen. New Outlook labels each meeting
/// for accessibility tools with its subject, times, date and location; this reads those labels
/// (see <see cref="OutlookLabel"/>). No API, no sign-in, nothing installed in Outlook, and the
/// copy never leaves this computer — it is not synced either.
/// </para>
/// <para>
/// <b>Outlook has to be open on the calendar, and not minimised.</b> Behind other windows is
/// fine — measured: a window covering it completely for 25 seconds changed nothing. Minimised,
/// Outlook discards its view and there is nothing to read; the copy from the last read stands,
/// and Settings says how old it is.
/// </para>
/// <para>
/// The copy is written in the shape a Power Automate calendar view has, so it is read by
/// <c>FileCalendar</c> like any calendar file, and everything above that — the dashboard, the
/// assistant — needs to know nothing about where it came from.
/// </para>
/// <para>
/// Each read replaces what was stored across the span of days it saw, and keeps the rest: a
/// switch from Week to Month view does not forget last week, and a meeting deleted from a day
/// that is on screen is gone at the next read.
/// </para>
/// </remarks>
public sealed class OutlookWatcher : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(3);

    private readonly Func<bool> _enabled;
    private readonly Timer _timer;
    private int _busy;

    public event Action<OutlookReadStatus>? Changed;

    public OutlookReadStatus Status { get; private set; } = new("Not read yet.");

    /// <summary>Where the copy lives. A calendar-file account points here.</summary>
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teezy", "outlook-calendar.json");

    public OutlookWatcher(Func<bool> enabled)
    {
        _enabled = enabled;
        _timer = new Timer(_ => _ = ReadAsync(), null, TimeSpan.FromSeconds(20), Every);
    }

    /// <summary>Reads now, off the UI thread. Returns once the copy is updated.</summary>
    public Task ReadAsync() => Task.Run(Read);

    private void Read()
    {
        if (!_enabled() || Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            // Classic Outlook first, when it is running: its own object model hands over every
            // meeting in the range, minimised or not, with nothing to parse.
            var today = DateTime.Today;
            if (ClassicOutlook.Read(today.AddDays(-7), today.AddDays(35)) is { } classic)
            {
                SaveRange(classic, today.AddDays(-7), today.AddDays(35));
                Report($"Read {classic.Count} meeting{(classic.Count == 1 ? "" : "s")} from classic Outlook at {DateTime.Now:h:mm tt}.");
                return;
            }

            var windows = OutlookWindows();
            if (windows.Count == 0)
            {
                Report("Outlook isn’t open. Open classic Outlook (it can stay minimised), or New Outlook on the Calendar. Using what it showed last time.", problem: true);
                return;
            }

            if (windows.All(w => IsIconic(w)))
            {
                Report("Outlook is minimised, so its calendar can’t be read. Using what it showed last time.", problem: true);
                return;
            }

            var seen = new List<CalendarEvent>();
            foreach (var window in windows.Where(w => !IsIconic(w)))
            {
                seen.AddRange(ReadWindow(window));
            }

            // Outlook, like any Chromium app, builds its accessibility tree only once something
            // asks for it — measured: the first look saw 18 elements, the second 381. So an
            // empty first read is asked again after a moment before it counts as empty.
            if (seen.Count == 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                foreach (var window in windows.Where(w => !IsIconic(w)))
                {
                    seen.AddRange(ReadWindow(window));
                }
            }

            var events = seen
                .DistinctBy(e => (e.Subject, e.Start, e.End))
                .ToList();

            if (events.Count == 0)
            {
                Report("Outlook is open but not showing the calendar. Switch it to Calendar (Week or Month) and leave it there.", problem: true);
                return;
            }

            Save(events);
            Report($"Read {events.Count} meeting{(events.Count == 1 ? "" : "s")} from Outlook at {DateTime.Now:h:mm tt}.");
        }
        catch (Exception e) when (e is ElementNotAvailableException or COMException or IOException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                      or InvalidOperationException or UnauthorizedAccessException)
        {
            Report($"Couldn’t read Outlook just now: {e.Message}", problem: true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Every top-level window belonging to New Outlook (olk.exe) — the main one and any calendar opened in its own window.</summary>
    private static List<IntPtr> OutlookWindows()
    {
        var ids = Process.GetProcessesByName("olk").Select(p => (uint)p.Id).ToHashSet();
        var found = new List<IntPtr>();
        if (ids.Count == 0) return found;

        EnumWindows((hwnd, lParam) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (ids.Contains(pid) && IsWindowVisible(hwnd)) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static IEnumerable<CalendarEvent> ReadWindow(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window);

        // Only buttons carry meeting labels; asking for them alone keeps the walk small.
        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement button in buttons)
        {
            string name;
            try { name = button.Current.Name; }
            catch (ElementNotAvailableException) { continue; }

            if (OutlookLabel.Parse(name) is { } meeting) yield return meeting;
        }
    }

    /// <summary>New Outlook's window: the span covered is the span of what was seen.</summary>
    private static void Save(IReadOnlyList<CalendarEvent> read) =>
        SaveRange(read, read.Min(e => e.Start.Date), read.Max(e => e.End.Date));

    /// <summary>Stores a read, replacing whatever was kept for the days it covered.</summary>
    private static void SaveRange(IReadOnlyList<CalendarEvent> read, DateTime from, DateTime to)
    {
        // Keep what was stored outside the days this read covered.
        var kept = Load().Where(e => e.End.Date < from || e.Start.Date > to);

        var items = new JsonArray();
        foreach (var e in kept.Concat(read).OrderBy(e => e.Start))
        {
            items.Add(new JsonObject
            {
                ["subject"] = e.Subject,
                ["start"] = e.IsAllDay ? e.Start.ToString("yyyy-MM-ddT00:00:00") : null,
                ["end"] = e.IsAllDay ? e.End.ToString("yyyy-MM-ddT00:00:00") : null,
                ["startWithTimeZone"] = e.IsAllDay ? null : e.Start.ToString("O"),
                ["endWithTimeZone"] = e.IsAllDay ? null : e.End.ToString("O"),
                ["isAllDay"] = e.IsAllDay,
                ["location"] = e.Location,
            });
        }

        var file = new JsonObject { ["readAt"] = DateTimeOffset.Now.ToString("O"), ["value"] = items };
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

        var temp = CachePath + ".writing";
        File.WriteAllText(temp, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, CachePath, overwrite: true);
    }

    private static IReadOnlyList<CalendarEvent> Load()
    {
        try
        {
            return File.Exists(CachePath) ? Connectors.FileCalendar.Parse(File.ReadAllText(CachePath)) : [];
        }
        catch (Exception e) when (e is IOException or CalendarUnavailableException)
        {
            return [];
        }
    }

    private void Report(string message, bool problem = false)
    {
        Status = new OutlookReadStatus(message, problem);
        Changed?.Invoke(Status);
    }

    public void Dispose() => _timer.Dispose();

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
