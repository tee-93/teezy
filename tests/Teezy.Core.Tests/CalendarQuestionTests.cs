using Shouldly;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Which spoken questions are about the diary, and which of them are cheap.</summary>
public class CalendarQuestionTests
{
    [Theory]
    [InlineData("what's next")]
    [InlineData("whats next")]
    [InlineData("what is next")]
    [InlineData("what's my next meeting")]
    [InlineData("what have I got coming up")]
    public void AsksWhatIsNext(string spoken) =>
        CalendarQuestion.Classify(spoken).ShouldBe(CalendarAsk.Next);

    [Theory]
    [InlineData("what's on today")]
    [InlineData("what's in my calendar today")]
    [InlineData("what does my day look like")]
    [InlineData("what meetings do I have today")]
    public void AsksAboutToday(string spoken) =>
        CalendarQuestion.Classify(spoken).ShouldBe(CalendarAsk.Today);

    [Theory]
    [InlineData("what's on tomorrow")]
    [InlineData("what meetings have I got tomorrow")]
    public void AsksAboutTomorrow(string spoken) =>
        CalendarQuestion.Classify(spoken).ShouldBe(CalendarAsk.Tomorrow);

    [Theory]
    [InlineData("am I free this afternoon")]
    [InlineData("when's my last meeting on Thursday")]
    [InlineData("how much of my calendar is meetings this week")]
    public void AsksSomethingOnlyTheSmarterTierCanAnswer(string spoken) =>
        CalendarQuestion.Classify(spoken).ShouldBe(CalendarAsk.Other);

    [Theory]
    [InlineData("how long does rice take")]
    [InlineData("turn the volume up")]
    [InlineData("open spotify")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNotAboutTheDiaryAtAll(string? spoken) =>
        CalendarQuestion.Classify(spoken).ShouldBe(CalendarAsk.None);

    [Fact]
    public void ThePossessiveSurvivesNormalising()
    {
        // Normalise turns every non-alphanumeric character into a space, so "what's next"
        // arrives as "what s next". Every phrase written the obvious way was unreachable until
        // the stranded "s" was rejoined, and nothing about reading the code showed it.
        CalendarQuestion.Classify("what's next").ShouldBe(CalendarAsk.Next);
        CalendarQuestion.Classify("what's on today").ShouldBe(CalendarAsk.Today);
    }

    [Fact]
    public void ASentenceMentioningAMeetingInPassingIsStillDictation()
    {
        // A softer version of the anchoring rule the commands use. Not perfect — this one is a
        // gate on vocabulary rather than a pattern — but it must not claim ordinary prose.
        CalendarQuestion.Classify("I'll send you the notes after").ShouldBe(CalendarAsk.None);
    }
}
