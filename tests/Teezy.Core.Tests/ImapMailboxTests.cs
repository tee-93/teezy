using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Mail;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>What can be checked about the Gmail reader without a mailbox to read.</summary>
/// <remarks>
/// The IMAP conversation itself is not faked here — MailKit's types are sealed around a live
/// connection, and a mock of them would test the mock. What is worth pinning down is the part
/// that decides whether to try at all, and what it says when it cannot.
/// </remarks>
public class ImapMailboxTests
{
    private static ImapMailbox Box(string? address, string? password) =>
        new(() => address, () => password);

    [Fact]
    public void ItIsGoogleSoAnswersCanNameIt() =>
        Box("a@gmail.com", "secret").Source.ShouldBe(MailSource.Google);

    [Theory]
    [InlineData(null, null)]
    [InlineData("a@gmail.com", null)]
    [InlineData(null, "secret")]
    [InlineData("a@gmail.com", "")]
    [InlineData("   ", "secret")]
    public void HalfSetUpIsNotSetUp(string? address, string? password)
    {
        // Both halves or nothing. A mailbox that claims to be connected gets asked, and a
        // failed connection is reported to the user as a mailbox that could not be reached —
        // which is a far more alarming thing to say than saying nothing at all.
        Box(address, password).IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void BothHalvesMeansConnected() =>
        Box("a@gmail.com", "abcdefghijklmnop").IsConnected.ShouldBeTrue();

    [Fact]
    public async Task AskingAnUnconfiguredMailboxSaysSoRatherThanDialling()
    {
        var failure = await Should.ThrowAsync<MailUnavailableException>(
            () => Box(null, null).RecentAsync(DateTimeOffset.Now.AddDays(-1), 10));

        // NeedsReconnect, because no amount of waiting fixes a missing app password.
        failure.NeedsReconnect.ShouldBeTrue();
        failure.Message.ShouldContain("not set up");
    }
}
