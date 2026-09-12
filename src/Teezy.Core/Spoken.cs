using System.Globalization;

namespace Teezy.Core;

/// <summary>Times and dates written the way a person says them.</summary>
/// <remarks>
/// <para>
/// Shared by everything that produces a sentence to be read aloud or glanced at in the pill,
/// so "2pm" is 2pm everywhere rather than "14:00" in one place and "2:00 PM" in another.
/// </para>
/// <para>
/// <b>Invariant culture throughout, on purpose.</b> The output is spoken English either way,
/// and a machine set to a 24-hour locale would otherwise have Teezy say "at 14:00", which
/// nobody does.
/// </para>
/// </remarks>
public static class Spoken
{
    /// <summary>A time as it is said: "9am", "9:30am", "2pm".</summary>
    public static string Clock(DateTimeOffset when)
    {
        var local = when.ToLocalTime();

        // "%h" rather than "h": a single-character format string is read as a standard
        // specifier, and there is no standard "h", so the bare form throws at runtime.
        var face = local.ToString(
            local.Minute == 0 ? "%h" : "h:mm", CultureInfo.InvariantCulture);

        return face + (local.Hour < 12 ? "am" : "pm");
    }

    /// <summary>Which day, when it is not this one — " tomorrow", " on Thursday", or "".</summary>
    public static string Day(DateTimeOffset when, DateTimeOffset now) =>
        DaysApart(when, now) switch
        {
            <= 0 => "",
            1 => " tomorrow",

            // Inside the week a day name places it instantly; beyond that "on Tuesday" is
            // ambiguous about which Tuesday.
            < 7 => $" on {when.ToLocalTime():dddd}",
            _ => $" on {when.ToLocalTime().ToString("d MMMM", CultureInfo.InvariantCulture)}",
        };

    /// <summary>A full date, with today and tomorrow named outright.</summary>
    /// <remarks>
    /// For material handed to a model rather than for speech. A day name alone leaves "is that
    /// today?" to be worked out from the clock, which is the one thing it most needs certainty
    /// about.
    /// </remarks>
    public static string LongDate(DateTimeOffset when, DateTimeOffset now)
    {
        var named = when.ToLocalTime().ToString("dddd d MMMM", CultureInfo.InvariantCulture);

        return DaysApart(when, now) switch
        {
            0 => $"{named} (today)",
            1 => $"{named} (tomorrow)",
            -1 => $"{named} (yesterday)",
            _ => named,
        };
    }

    private static int DaysApart(DateTimeOffset when, DateTimeOffset now) =>
        (when.ToLocalTime().Date - now.ToLocalTime().Date).Days;
}
