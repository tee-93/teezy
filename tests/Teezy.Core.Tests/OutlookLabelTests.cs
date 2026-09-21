using Shouldly;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Labels exactly as New Outlook reported them on 2026-09-22.</summary>
public class OutlookLabelTests
{
    [Fact]
    public void ReadsAMeetingWithALocation()
    {
        var e = OutlookLabel.Parse(
            "Reservation at Longboard Bar, 6:15 PM to 7:15 PM, Wednesday, September 23, 2026, Longboard Bar, 80 The Strand \"Headlands\", Townsville, QLD, 4810, Free, Private")!;

        e.Subject.ShouldBe("Reservation at Longboard Bar");
        e.Start.DateTime.ShouldBe(new DateTime(2026, 9, 23, 18, 15, 0));
        e.End.DateTime.ShouldBe(new DateTime(2026, 9, 23, 19, 15, 0));
        e.IsAllDay.ShouldBeFalse();
        e.Location.ShouldBe("Longboard Bar, 80 The Strand \"Headlands\", Townsville, QLD, 4810");
    }

    [Fact]
    public void ASubjectMayContainCommasAndTimes()
    {
        var e = OutlookLabel.Parse(
            "Flight NZ146 12:20pm, Mon 14 Sep, 12:20 PM to 3:25 PM, Monday, September 14, 2026, Busy, Private")!;

        e.Subject.ShouldBe("Flight NZ146 12:20pm, Mon 14 Sep");
        e.Start.DateTime.ShouldBe(new DateTime(2026, 9, 14, 12, 20, 0));
        e.Location.ShouldBeNull();
    }

    [Fact]
    public void StatusWordsAreNotALocation() =>
        OutlookLabel.Parse("Water Filter Replacement, 8:00 AM to 9:00 AM, Thursday, September 10, 2026, Busy, Recurring event")!
            .Location.ShouldBeNull();

    [Fact]
    public void ReadsAnAllDayEvent()
    {
        var e = OutlookLabel.Parse("Bella week, all day event, Friday, September 25, 2026, Free, Recurring event")!;

        e.IsAllDay.ShouldBeTrue();
        e.Start.DateTime.ShouldBe(new DateTime(2026, 9, 25));
        e.End.DateTime.ShouldBe(new DateTime(2026, 9, 26));
    }

    [Fact]
    public void AnAllDayEventCanSpanDays()
    {
        var e = OutlookLabel.Parse(
            "Novotel Auckland Airport, all day event, Monday, September 14, 2026 to Friday, September 18, 2026, Free")!;

        e.Start.DateTime.ShouldBe(new DateTime(2026, 9, 14));
        e.End.DateTime.ShouldBe(new DateTime(2026, 9, 19));
    }

    [Fact]
    public void AustralianDateOrderIsReadToo()
    {
        var e = OutlookLabel.Parse("Toolbox talk, 7:30 am to 8:00 am, Wednesday, 23 September 2026, Site office, Busy")!;

        e.Start.DateTime.ShouldBe(new DateTime(2026, 9, 23, 7, 30, 0));
        e.Location.ShouldBe("Site office");
    }

    [Fact]
    public void TwentyFourHourTimesAreReadToo() =>
        OutlookLabel.Parse("Standup, 09:00 to 09:15, Tuesday, 22 September 2026, Busy")!
            .End.DateTime.ShouldBe(new DateTime(2026, 9, 22, 9, 15, 0));

    [Fact]
    public void CancelledMeetingsAreSkipped() =>
        OutlookLabel.Parse("Canceled: Supplier call, 10:00 AM to 10:30 AM, Tuesday, September 22, 2026, Free").ShouldBeNull();

    [Theory]
    [InlineData("calendar view, current time: Tue 9:38 AM")]
    [InlineData("September 2026, select to change the month")]
    [InlineData("22, September, 2026")]
    [InlineData("")]
    public void OtherLabelsAreNotMeetings(string label) => OutlookLabel.Parse(label).ShouldBeNull();
}
