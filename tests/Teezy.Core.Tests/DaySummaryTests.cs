using Shouldly;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.Mail;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>The line the dashboard opens with.</summary>
public class DaySummaryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 9, 10, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14)));

    private static CalendarEvent Meeting(string subject, int hour)
    {
        var start = Now.Date.AddHours(hour);
        var offset = TimeZoneInfo.Local.GetUtcOffset(start);

        return new CalendarEvent(
            subject,
            new DateTimeOffset(start, offset),
            new DateTimeOffset(start.AddHours(1), offset),
            false, null, CalendarSource.Microsoft);
    }

    private static MailMessage Message(bool unread) =>
        new("Subject", "Someone", "a@b.com", Now.AddHours(-1), null, unread, MailSource.Microsoft);

    private static CalendarReading Diary(params CalendarEvent[] events) => new(events, []);
    private static MailReading Inbox(params MailMessage[] messages) => new(messages, []);

    [Fact]
    public void SaysBothHalvesInOneSentence()
    {
        var said = DaySummary.For(
            Diary(Meeting("Review", 14), Meeting("Drinks", 18)),
            Inbox(Message(true), Message(true), Message(true)),
            Now);

        said.ShouldBe("Two things left today, and three unread.");
    }

    [Fact]
    public void AQuietDaySaysSoWithoutSoundingBroken()
    {
        DaySummary.For(Diary(), Inbox(Message(false)), Now)
            .ShouldBe("Nothing else on today, and nothing unread.");
    }

    [Fact]
    public void WhatIsAlreadyOverDoesNotCount()
    {
        // The window is the whole day, so the morning is still in the reading and has to be
        // filtered — otherwise the line reports meetings that already happened.
        DaySummary.For(Diary(Meeting("Stand-up", 8), Meeting("Review", 14)), null, Now)
            .ShouldBe("One thing left today.");
    }

    [Fact]
    public void OnlyOneHalfConnectedIsOneClause()
    {
        DaySummary.For(Diary(Meeting("Review", 14)), null, Now).ShouldBe("One thing left today.");
        DaySummary.For(null, Inbox(Message(true), Message(true)), Now).ShouldBe("Two unread.");
    }

    [Fact]
    public void NothingConnectedSaysWhatToDoInstead()
    {
        // Most of Teezy has never needed an account, so an empty dashboard is not a failure
        // and should not read as one.
        DaySummary.For(null, null, Now).ShouldBe("Hold your assistant key and ask me something.");
    }

    [Fact]
    public void AnUnreachableCalendarIsNeverSummarisedAsAFreeDay()
    {
        var unreachable = new CalendarReading([], [CalendarSource.Microsoft]);

        var said = DaySummary.For(unreachable, Inbox(Message(true)), Now);

        // The clause stands aside rather than claiming the day is clear, and the reader is
        // told which half of the answer is missing.
        said.ShouldNotContain("nothing else");
        said.ShouldBe("One unread. One account couldn’t be reached.");
    }

    [Fact]
    public void TwoUnreachableAccountsAreCountedTogether()
    {
        var diary = new CalendarReading([], [CalendarSource.Microsoft]);
        var mail = new MailReading([], [MailSource.Google]);

        // And nothing else. Suggesting they ask a question over the top of it would bury the
        // one fact worth having.
        DaySummary.For(diary, mail, Now).ShouldBe("2 accounts couldn’t be reached.");
    }

    [Fact]
    public void AnAllDayEventCountsEvenThoughItStartedAtMidnight()
    {
        var leave = new CalendarEvent(
            "Leave", Now.Date, Now.Date.AddDays(1), true, null, CalendarSource.Microsoft);

        DaySummary.For(new CalendarReading([leave], []), null, Now)
            .ShouldBe("One thing left today.");
    }
}
