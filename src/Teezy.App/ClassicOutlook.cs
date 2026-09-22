using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Teezy.Core.Calendar;

namespace Teezy.App;

/// <summary>Reads the calendar from classic Outlook, through its own object model.</summary>
/// <remarks>
/// <para>
/// <b>The straightforward route, when classic Outlook is on the machine.</b> Classic Outlook
/// answers questions from other programs on the same computer through COM — its documented,
/// supported interface — so the calendar comes straight from Outlook's own store: every meeting
/// in the range, whatever view is showing, with Outlook minimised or behind other windows. Nothing
/// goes over the network and no organisation's consent is involved; it is the same interface
/// add-ins and scripts on the desktop have always used.
/// </para>
/// <para>
/// <b>Only a running Outlook is asked.</b> Starting it from here would launch a hidden copy that
/// can stall on a profile or sign-in prompt nobody can see, so if it is not running the answer is
/// simply "not available" and the caller falls back.
/// </para>
/// <para>
/// Read-only by construction: nothing here writes to an item, and only the plain appointment
/// fields are read — not attendees or organiser, which is what Outlook's security guard would
/// otherwise stop to ask about.
/// </para>
/// </remarks>
internal static class ClassicOutlook
{
    private const int CalendarFolder = 9;          // olFolderCalendar
    private const int MeetingCancelled = 5;        // olMeetingCanceled
    private const int MeetingReceivedCancelled = 7; // olMeetingReceivedAndCanceled

    /// <summary>The meetings between two times, or null if classic Outlook is not running.</summary>
    /// <exception cref="COMException">Outlook is running but refused, e.g. mid-sync or busy with a dialog.</exception>
    public static IReadOnlyList<CalendarEvent>? Read(DateTime from, DateTime to)
    {
        if (Running() is not { } outlook) return null;

        dynamic? ns = null, folder = null, items = null, found = null;
        try
        {
            ns = outlook.GetNamespace("MAPI");
            folder = ns.GetDefaultFolder(CalendarFolder);
            items = folder.Items;

            // Recurring meetings are expanded into their occurrences only with this on, and only
            // if the list is sorted by start first — Outlook's documented order of operations.
            items.IncludeRecurrences = true;
            items.Sort("[Start]");

            // Outlook's filter reads dates in this computer's regional format.
            var filter = string.Format(CultureInfo.CurrentCulture, "[Start] < '{0:g}' AND [End] > '{1:g}'", to, from);
            found = items.Restrict(filter);

            // GetFirst/GetNext, not foreach or Count: with recurrences expanded Outlook cannot say
            // how many there are, and its documentation walks the list this way. The cap is a
            // guard against a filter that somehow matched an endless series.
            var events = new List<CalendarEvent>();
            var walked = 0;
            for (dynamic? item = found.GetFirst(); item is not null && walked < 2000; item = found.GetNext(), walked++)
            {
                try
                {
                    // Only appointments; a calendar can hold other item types.
                    if (item.Class != 26) continue; // olAppointment

                    int status = item.MeetingStatus;
                    if (status is MeetingCancelled or MeetingReceivedCancelled) continue;

                    string subject = item.Subject ?? "(no subject)";
                    if (subject.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase)
                        || subject.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase)) continue;

                    DateTime start = item.Start;
                    DateTime end = item.End;
                    bool allDay = item.AllDayEvent;
                    string? location = item.Location;

                    events.Add(new CalendarEvent(
                        subject,
                        Local(start),
                        Local(end),
                        allDay,
                        string.IsNullOrWhiteSpace(location) ? null : location,
                        CalendarSource.File));
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            return events;
        }
        finally
        {
            Release(found);
            Release(items);
            Release(folder);
            Release(ns);
            Marshal.ReleaseComObject(outlook);
        }
    }

    /// <summary>Whether classic Outlook is running, without starting it.</summary>
    public static bool IsRunning
    {
        get
        {
            if (Running() is not { } outlook) return false;
            Marshal.ReleaseComObject(outlook);
            return true;
        }
    }

    internal static dynamic? Running()
    {
        if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0) return null;   // not installed
        return GetActiveObject(ref clsid, IntPtr.Zero, out var instance) == 0 ? instance : null;
    }

    internal static DateTimeOffset Local(DateTime time)
    {
        var local = DateTime.SpecifyKind(time, DateTimeKind.Local);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    internal static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    // Marshal.GetActiveObject is not in modern .NET; these are the two calls it made.
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}
