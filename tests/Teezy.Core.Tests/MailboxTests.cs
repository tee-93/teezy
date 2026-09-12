using Shouldly;
using Teezy.Calendar;
using Teezy.Core.Mail;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Merging mailboxes, and reading what Graph sends back.</summary>
public class MailboxTests
{
    private sealed class FakeMailbox(MailSource source) : IMailbox
    {
        public MailSource Source { get; } = source;
        public bool IsConnected { get; set; } = true;
        public List<MailMessage> Messages { get; } = [];
        public string? FailWith { get; set; }
        public int AskedFor { get; private set; }

        public Task<IReadOnlyList<MailMessage>> RecentAsync(
            DateTimeOffset since, int atMost, CancellationToken ct = default)
        {
            AskedFor = atMost;

            if (FailWith is { } why) throw new MailUnavailableException(why);

            return Task.FromResult<IReadOnlyList<MailMessage>>(Messages);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private static MailMessage At(string who, int minutesAgo, MailSource source) =>
        new("Subject", who, $"{who}@example.com", Now.AddMinutes(-minutesAgo), null, true, source);

    private static Task<MailReading> Read(int atMost, params IMailbox[] boxes) =>
        new CombinedMailbox(boxes).RecentAsync(Now.AddDays(-1), atMost);

    [Fact]
    public async Task MergesTwoMailboxesNewestFirst()
    {
        var work = new FakeMailbox(MailSource.Microsoft);
        work.Messages.Add(At("Colleague", 60, MailSource.Microsoft));

        var home = new FakeMailbox(MailSource.Google);
        home.Messages.Add(At("Origin", 5, MailSource.Google));

        var reading = await Read(50, work, home);

        reading.Messages.Select(m => m.Who).ShouldBe(["Origin", "Colleague"]);
    }

    [Fact]
    public async Task OneMailboxFailingStillAnswersFromTheOther()
    {
        var work = new FakeMailbox(MailSource.Microsoft) { FailWith = "token expired" };

        var home = new FakeMailbox(MailSource.Google);
        home.Messages.Add(At("Origin", 5, MailSource.Google));

        var reading = await Read(50, work, home);

        reading.Messages.Count.ShouldBe(1);
        reading.Unavailable.ShouldBe([MailSource.Microsoft]);
        reading.NothingAnswered.ShouldBeFalse();
    }

    [Fact]
    public async Task EverythingFailingIsNotAnEmptyInbox()
    {
        var work = new FakeMailbox(MailSource.Microsoft) { FailWith = "no" };
        var home = new FakeMailbox(MailSource.Google) { FailWith = "no" };

        (await Read(50, work, home)).NothingAnswered.ShouldBeTrue();
    }

    [Fact]
    public async Task TheCeilingHoldsAcrossTwoMailboxes()
    {
        var work = new FakeMailbox(MailSource.Microsoft);
        var home = new FakeMailbox(MailSource.Google);

        for (var i = 0; i < 10; i++)
        {
            work.Messages.Add(At($"W{i}", i, MailSource.Microsoft));
            home.Messages.Add(At($"H{i}", i, MailSource.Google));
        }

        var reading = await Read(10, work, home);

        // Two mailboxes must not between them return twice what was asked for.
        reading.Messages.Count.ShouldBe(10);
        work.AskedFor.ShouldBe(10);
    }

    [Fact]
    public async Task DisconnectedMailboxesAreNotAsked()
    {
        var never = new FakeMailbox(MailSource.Google) { IsConnected = false };

        var reading = await Read(50, never);

        never.AskedFor.ShouldBe(0);
        reading.Messages.ShouldBeEmpty();
    }

    // ---- reading Graph's reply ----

    private const string Body = """
        {
          "value": [
            {
              "subject": "Your bill is ready",
              "receivedDateTime": "2026-09-14T08:30:00Z",
              "isRead": false,
              "bodyPreview": "Your September statement is attached.",
              "from": { "emailAddress": { "name": "Origin Energy", "address": "no-reply@originenergy.com.au" } }
            }
          ]
        }
        """;

    [Fact]
    public void ReadsAMessage()
    {
        var messages = GraphMailbox.Read(Body);

        messages.Count.ShouldBe(1);
        messages[0].Subject.ShouldBe("Your bill is ready");
        messages[0].Who.ShouldBe("Origin Energy");
        messages[0].IsUnread.ShouldBeTrue();
        messages[0].Preview.ShouldBe("Your September statement is attached.");
        messages[0].Received.ToUniversalTime()
            .ShouldBe(new DateTimeOffset(2026, 9, 14, 8, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TheDomainComesFromTheAddressNotTheName()
    {
        // A display name is chosen by the sender and is the easiest thing in a message to
        // forge. Anything deciding who mail is really from has to use the address.
        var messages = GraphMailbox.Read(Body);

        messages[0].Domain.ShouldBe("originenergy.com.au");
    }

    [Fact]
    public void AForgedDisplayNameDoesNotChangeTheDomain()
    {
        const string phish = """
            {
              "value": [ {
                "subject": "Verify your account",
                "receivedDateTime": "2026-09-14T08:30:00Z",
                "isRead": false,
                "from": { "emailAddress": { "name": "Origin Energy", "address": "billing@totally-not-origin.ru" } }
              } ]
            }
            """;

        var messages = GraphMailbox.Read(phish);

        messages[0].Who.ShouldBe("Origin Energy");
        messages[0].Domain.ShouldBe("totally-not-origin.ru");
    }

    [Fact]
    public void AMessageWithNoReadFlagIsTreatedAsRead()
    {
        const string vague = """
            {
              "value": [ {
                "subject": "Hello",
                "receivedDateTime": "2026-09-14T08:30:00Z",
                "from": { "emailAddress": { "name": "Someone", "address": "a@b.com" } }
              } ]
            }
            """;

        // A message Graph declines to describe should not be announced as something new.
        GraphMailbox.Read(vague)[0].IsUnread.ShouldBeFalse();
    }

    [Fact]
    public void AMessageWithNoSenderStillReads()
    {
        const string anonymous = """
            {
              "value": [ {
                "subject": "System notice",
                "receivedDateTime": "2026-09-14T08:30:00Z",
                "isRead": false
              } ]
            }
            """;

        var messages = GraphMailbox.Read(anonymous);

        messages.Count.ShouldBe(1);
        messages[0].Who.ShouldBe("");
        messages[0].Domain.ShouldBe("");
    }

    [Fact]
    public void AnEmptyInboxIsAnEmptyListNotAFailure() =>
        GraphMailbox.Read("""{ "value": [] }""").ShouldBeEmpty();

    [Fact]
    public void NonsenseIsReportedAsUnavailable() =>
        Should.Throw<MailUnavailableException>(() => GraphMailbox.Read("not json"));
}
