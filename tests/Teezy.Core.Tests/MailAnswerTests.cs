using Shouldly;
using Teezy.Core.Mail;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>The sentences Teezy composes about an inbox without asking anyone.</summary>
public class MailAnswerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 9, 10, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14)));

    private static MailMessage From(
        string who, bool unread = true, int hoursAgo = 1, string address = "a@b.com") =>
        new("Subject", who, address, Now.AddHours(-hoursAgo), null, unread, MailSource.Microsoft);

    private static MailReading Inbox(params MailMessage[] messages) => new(messages, []);

    private static string? Answer(MailAsk ask, MailReading reading) =>
        MailAnswer.For(ask, reading, Now);

    // ---- unread ----

    [Fact]
    public void NothingUnreadSaysSo() =>
        Answer(MailAsk.Unread, Inbox(From("Origin", unread: false))).ShouldBe("Nothing unread.");

    [Fact]
    public void OneUnreadNamesTheSender() =>
        Answer(MailAsk.Unread, Inbox(From("Origin Energy")))
            .ShouldBe("One unread, from Origin Energy.");

    [Fact]
    public void SeveralUnreadCountAndName() =>
        Answer(MailAsk.Unread, Inbox(From("Origin Energy"), From("Telstra")))
            .ShouldBe("2 unread, from Origin Energy and Telstra.");

    [Fact]
    public void RepeatedSendersAreOneFactNotSeveral()
    {
        var reading = Inbox(From("GitHub"), From("GitHub"), From("GitHub"));

        // Six notifications from one sender is one thing worth saying, not six.
        Answer(MailAsk.Unread, reading).ShouldBe("3 unread, from GitHub.");
    }

    [Fact]
    public void TooManySendersAreCounted()
    {
        var reading = Inbox(From("A"), From("B"), From("C"), From("D"), From("E"));

        Answer(MailAsk.Unread, reading).ShouldBe("5 unread, from A, B, C and 2 others.");
    }

    [Fact]
    public void SubjectsAreNeverReadOut()
    {
        var reading = Inbox(new MailMessage(
            "URGENT: verify your account now",
            "Your Bank",
            "phish@example.com",
            Now.AddHours(-1),
            "Click here immediately",
            true,
            MailSource.Microsoft));

        // Subject lines are the field most often written to bait a reaction, and a spoken
        // summary is not the place to repeat one.
        var answer = Answer(MailAsk.Unread, reading)!;

        answer.ShouldNotContain("URGENT");
        answer.ShouldNotContain("Click here");
        answer.ShouldBe("One unread, from Your Bank.");
    }

    // ---- today ----

    [Fact]
    public void AnEmptyDaySaysSo() =>
        Answer(MailAsk.Today, MailReading.Empty).ShouldBe("Nothing in today.");

    [Fact]
    public void TodayCountsAndSaysHowManyAreUnread()
    {
        var reading = Inbox(
            From("Origin Energy"),
            From("Telstra", unread: false),
            From("GitHub", unread: false));

        Answer(MailAsk.Today, reading)
            .ShouldBe("3 emails today, 1 unread, from Origin Energy, Telstra and GitHub.");
    }

    [Fact]
    public void TodayDoesNotSayUnreadWhenEverythingIs()
    {
        Answer(MailAsk.Today, Inbox(From("Origin Energy")))
            .ShouldBe("One email today, from Origin Energy.");
    }

    [Fact]
    public void YesterdayIsNotToday()
    {
        // The window is a week for unread, so older messages are present and must be filtered
        // rather than counted.
        Answer(MailAsk.Today, Inbox(From("Origin Energy", hoursAgo: 30)))
            .ShouldBe("Nothing in today.");
    }

    // ---- mailboxes that did not answer ----

    [Fact]
    public void AMailboxThatCouldNotBeReachedIsAdmitted()
    {
        var reading = new MailReading([From("Origin Energy")], [MailSource.Google]);

        Answer(MailAsk.Unread, reading)
            .ShouldBe("One unread, from Origin Energy. I couldn’t reach your Gmail mail.");
    }

    [Fact]
    public void NothingReachableIsNeverReportedAsAnEmptyInbox()
    {
        var reading = new MailReading([], [MailSource.Microsoft]);

        Answer(MailAsk.Unread, reading).ShouldBe("I couldn’t reach your Microsoft mail.");
    }

    // ---- windows ----

    [Fact]
    public void UnreadLooksBackAWeek()
    {
        // An unread message from Tuesday is still unread, and an answer that ignored it would
        // be wrong in the direction that matters.
        var (since, _) = MailAnswer.Window(MailAsk.Unread, Now);

        (Now - since).ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void TodayStartsAtMidnight()
    {
        var (since, _) = MailAnswer.Window(MailAsk.Today, Now);

        since.ToLocalTime().TimeOfDay.ShouldBe(TimeSpan.Zero);
        since.ToLocalTime().Date.ShouldBe(Now.ToLocalTime().Date);
    }

    [Fact]
    public void AQuestionOnlyTheSmarterTierCanTakeIsLeftUnanswered() =>
        Answer(MailAsk.Other, Inbox(From("Origin Energy"))).ShouldBeNull();
}
