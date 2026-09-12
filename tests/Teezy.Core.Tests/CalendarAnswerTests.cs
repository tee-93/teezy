using Shouldly;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>
/// The sentences Teezy composes itself, which are the ones nearly every question gets.
/// </summary>
/// <remarks>
/// Pinned down closely on purpose. This text is read aloud and glanced at above the taskbar,
/// so its exact shape is the feature — a wording regression here is not cosmetic, it is the
/// answer getting worse.
/// </remarks>
public class CalendarAnswerTests
{
    /// <summary>A Monday morning, fixed so every relative phrase is checkable.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 9, 10, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14)));

    private static DateTimeOffset At(int hour, int minute = 0, int addDays = 0)
    {
        var when = Now.Date.AddDays(addDays).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(when, TimeZoneInfo.Local.GetUtcOffset(when));
    }

    private static CalendarEvent Meeting(
        string subject, DateTimeOffset start, DateTimeOffset end, bool allDay = false) =>
        new(subject, start, end, allDay, null, CalendarSource.Microsoft);

    private static CalendarReading Diary(params CalendarEvent[] events) => new(events, []);

    private static string? Answer(CalendarAsk ask, CalendarReading reading) =>
        CalendarAnswer.For(ask, reading, Now);

    // ---- what's next ----

    [Fact]
    public void SaysWhatYouAreInAndWhenItEnds()
    {
        var reading = Diary(Meeting("Stand-up", At(9), At(9, 30)));

        Answer(CalendarAsk.Next, reading).ShouldBe("You’re in Stand-up until 9:30am.");
    }

    [Fact]
    public void SaysHowLongAwayWhenItIsSoon()
    {
        var reading = Diary(Meeting("Stand-up", At(9, 30), At(9, 45)));

        // Twenty minutes away is far more useful than the time on the clock, and it is what
        // the question was really asking.
        Answer(CalendarAsk.Next, reading).ShouldBe("Stand-up in 20 minutes.");
    }

    [Fact]
    public void SaysTheTimeWhenItIsFurtherOff()
    {
        var reading = Diary(Meeting("Review", At(14), At(15)));

        Answer(CalendarAsk.Next, reading).ShouldBe("Next is Review at 2pm.");
    }

    [Fact]
    public void NamesTheDayWhenItIsNotToday()
    {
        var reading = Diary(Meeting("Review", At(14, 0, 1), At(15, 0, 1)));

        Answer(CalendarAsk.Next, reading).ShouldBe("Next is Review tomorrow at 2pm.");
    }

    [Fact]
    public void NamesTheWeekdayLaterInTheWeek()
    {
        var reading = Diary(Meeting("Retro", At(11, 0, 3), At(12, 0, 3)));

        Answer(CalendarAsk.Next, reading).ShouldBe("Next is Retro on Thursday at 11am.");
    }

    [Fact]
    public void AnAllDayEventIsNeverWhatIsNext()
    {
        // "You're on leave" does not answer "what's next", and letting it would claim the slot
        // for the entire day.
        var reading = Diary(
            Meeting("Leave", At(0), At(0, 0, 1), allDay: true),
            Meeting("Review", At(14), At(15)));

        Answer(CalendarAsk.Next, reading).ShouldBe("Next is Review at 2pm.");
    }

    [Fact]
    public void AnEmptyWeekSaysSo()
    {
        Answer(CalendarAsk.Next, CalendarReading.Empty)
            .ShouldBe("Nothing in your calendar for the next week.");
    }

    // ---- today and tomorrow ----

    [Fact]
    public void ListsTheDayAndCountsIt()
    {
        var reading = Diary(
            Meeting("Stand-up", At(9, 30), At(9, 45)),
            Meeting("Review", At(14), At(15)),
            Meeting("Drinks", At(18), At(20)));

        Answer(CalendarAsk.Today, reading)
            .ShouldBe("Three things today: Stand-up at 9:30am, Review at 2pm and Drinks at 6pm.");
    }

    [Fact]
    public void StopsNamingAfterFourAndCountsTheRest()
    {
        var reading = Diary(
            Meeting("One", At(10), At(10, 30)),
            Meeting("Two", At(11), At(11, 30)),
            Meeting("Three", At(12), At(12, 30)),
            Meeting("Four", At(13), At(13, 30)),
            Meeting("Five", At(14), At(14, 30)),
            Meeting("Six", At(15), At(15, 30)));

        // A packed day read out in full is a recital, not an answer.
        Answer(CalendarAsk.Today, reading).ShouldBe(
            "Six things today: One at 10am, Two at 11am, Three at 12pm, Four at 1pm, and 2 more.");
    }

    [Fact]
    public void WhatIsAlreadyOverIsNotReadBack()
    {
        var reading = Diary(
            Meeting("Early call", At(8), At(8, 30)),
            Meeting("Review", At(14), At(15)));

        Answer(CalendarAsk.Today, reading).ShouldBe("One thing today: Review at 2pm.");
    }

    [Fact]
    public void AnAllDayEventIsSaidAsAllDay()
    {
        var reading = Diary(Meeting("Leave", At(0), At(0, 0, 1), allDay: true));

        // Kept despite having started at midnight, because it is still on.
        Answer(CalendarAsk.Today, reading).ShouldBe("One thing today: Leave all day.");
    }

    [Fact]
    public void AnEmptyDaySaysNothingElseRatherThanNothing()
    {
        // The morning may well have been full, and "nothing today" at four in the afternoon
        // sounds like a calendar that is not working.
        Answer(CalendarAsk.Today, CalendarReading.Empty).ShouldBe("Nothing else today.");
    }

    [Fact]
    public void TomorrowIsListedWhole()
    {
        var reading = Diary(
            Meeting("Planning", At(10, 0, 1), At(11, 0, 1)),
            Meeting("One to one", At(15, 30, 1), At(16, 0, 1)));

        Answer(CalendarAsk.Tomorrow, reading)
            .ShouldBe("Two things tomorrow: Planning at 10am and One to one at 3:30pm.");
    }

    [Fact]
    public void AnEmptyTomorrowSaysSo()
    {
        Answer(CalendarAsk.Tomorrow, CalendarReading.Empty).ShouldBe("Nothing tomorrow.");
    }

    // ---- accounts that did not answer ----

    [Fact]
    public void AnAccountThatCouldNotBeReachedIsAdmitted()
    {
        var reading = new CalendarReading(
            [Meeting("Review", At(14), At(15))], [CalendarSource.Google]);

        Answer(CalendarAsk.Today, reading)
            .ShouldBe("One thing today: Review at 2pm. I couldn’t reach your Google calendar.");
    }

    [Fact]
    public void NothingReachableIsNeverReportedAsAFreeDay()
    {
        var reading = new CalendarReading([], [CalendarSource.Microsoft]);

        // The single most harmful thing this feature could say. An unreachable calendar and an
        // empty one are opposite answers, and collapsing them walks someone into a meeting.
        Answer(CalendarAsk.Today, reading).ShouldBe("I couldn’t reach your Microsoft calendar.");
    }

    // ---- windows ----

    [Fact]
    public void TodayRunsFromNowUntilMidnight()
    {
        var (from, to) = CalendarAnswer.Window(CalendarAsk.Today, Now);

        from.ShouldBe(Now);
        to.ToLocalTime().TimeOfDay.ShouldBe(TimeSpan.Zero);
        to.ToLocalTime().Date.ShouldBe(Now.ToLocalTime().Date.AddDays(1));
    }

    [Fact]
    public void TomorrowIsTheWholeOfTomorrowAndNoneOfToday()
    {
        var (from, to) = CalendarAnswer.Window(CalendarAsk.Tomorrow, Now);

        from.ToLocalTime().Date.ShouldBe(Now.ToLocalTime().Date.AddDays(1));
        (to - from).ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void NextLooksAWeekAheadSoAFridayAfternoonIsNotAnEmptyAnswer()
    {
        var (from, to) = CalendarAnswer.Window(CalendarAsk.Next, Now);

        from.ShouldBe(Now);
        (to - from).ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void AQuestionOnlyTheSmarterTierCanTakeIsLeftUnanswered()
    {
        // Null is the signal to escalate, and is what keeps "am I free at half three" out of
        // the composed phrasings rather than answered badly by them.
        Answer(CalendarAsk.Other, Diary(Meeting("Review", At(14), At(15)))).ShouldBeNull();
    }
}
