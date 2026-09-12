using Shouldly;
using Teezy.Core.Mail;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Which spoken questions are about the inbox — and, more importantly, which are not.</summary>
public class MailQuestionTests
{
    [Theory]
    [InlineData("any new email")]
    [InlineData("have I got any unread")]
    [InlineData("anything new in my inbox")]
    [InlineData("what's unread")]
    public void AsksAboutUnread(string spoken) =>
        MailQuestion.Classify(spoken).ShouldBe(MailAsk.Unread);

    [Theory]
    [InlineData("what emails came in today")]
    [InlineData("what email came in this morning")]
    [InlineData("any email today")]
    [InlineData("any mail come in today")]
    public void AsksAboutToday(string spoken) =>
        MailQuestion.Classify(spoken).ShouldBe(MailAsk.Today);

    [Fact]
    public void APhrasingThatNeverNamesMailIsNotClaimed()
    {
        // "What's come in this morning" is a perfectly natural way to ask about mail, and it
        // is also how someone asks about deliveries, orders or the day's meetings. The gate
        // declines it deliberately: guessing wrong sends a slice of the mailbox to answer a
        // question that was never about mail.
        MailQuestion.Classify("what's come in this morning").ShouldBe(MailAsk.None);
    }

    [Theory]
    [InlineData("has the electricity bill arrived in my email")]
    [InlineData("did anyone email me about the invoice")]
    public void AsksSomethingOnlyTheSmarterTierCanAnswer(string spoken) =>
        MailQuestion.Classify(spoken).ShouldBe(MailAsk.Other);

    [Theory]
    [InlineData("turn the volume up")]
    [InlineData("what's on today")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNotAboutMailAtAll(string? spoken) =>
        MailQuestion.Classify(spoken).ShouldBe(MailAsk.None);

    [Theory]
    [InlineData("send him a message about the invoice")]
    [InlineData("I'll drop you a message later")]
    [InlineData("the message was that we need to move faster")]
    [InlineData("can you post that to the team")]
    public void OrdinaryDictationContainingMessageIsNotClaimed(string spoken)
    {
        // The gate is stricter than the diary's on purpose. "Mail" and "message" are everyday
        // words, and claiming one of these would swallow text meant for a text box — and then
        // send a slice of the mailbox to answer it.
        MailQuestion.Classify(spoken).ShouldBe(MailAsk.None);
    }

    [Fact]
    public void TheWeakerWordsCountWhenTheSentenceIsAsking()
    {
        MailQuestion.Classify("have I got any mail").ShouldBe(MailAsk.Other);
        MailQuestion.Classify("any new mail").ShouldBe(MailAsk.Unread);
    }

    [Fact]
    public void ThePossessiveSurvivesNormalising()
    {
        // Normalise turns the apostrophe into a space, so "what's" arrives as "what s".
        MailQuestion.Classify("what's in my inbox").ShouldBe(MailAsk.Other);
    }
}
