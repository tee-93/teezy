using Shouldly;
using Teezy.Core.Quotes;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Recording a quote out loud, which is where most of them are sent from.</summary>
public class QuoteVoiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static ParsedQuote? Heard(string said) => QuoteVoice.Add(said, Today);

    [Fact]
    public void TheEverydayPhrasingIsUnderstood()
    {
        var quote = Heard("quoted Hunter Builders four thousand two hundred for door hardware").ShouldNotBeNull();

        quote.Customer.ShouldBe("Hunter Builders");
        quote.AmountCents.ShouldBe(420_000);
        quote.What.ShouldBe("door hardware");
    }

    [Theory]
    [InlineData("I just quoted Orikan nine hundred dollars for readers")]
    [InlineData("sent a quote to Orikan for nine hundred dollars for readers")]
    [InlineData("add a quote for Orikan nine hundred dollars readers")]
    public void TheOtherWaysPeopleSayItAreTooUnderstood(string said)
    {
        var quote = Heard(said).ShouldNotBeNull();

        quote.Customer.ShouldBe("Orikan");
        quote.AmountCents.ShouldBe(90_000);
    }

    [Fact]
    public void GstWordsDoNotBecomePartOfTheJob()
    {
        var quote = Heard("quoted Cessnock Hospital twelve thousand dollars plus GST for closers").ShouldNotBeNull();

        quote.AmountCents.ShouldBe(1_200_000);
        quote.What.ShouldBe("closers");
    }

    [Fact]
    public void SomethingThatIsNotAQuoteIsLeftForTheRestOfTheChain()
    {
        Heard("what tasks do I have today").ShouldBeNull();
        Heard("quote of the day").ShouldBeNull();
        Heard("quoted Hunter Builders for door hardware").ShouldBeNull();   // no value said
    }

    [Fact]
    public void WhatItSaysBackNamesTheMoneyAndWhenItWillBeChased()
    {
        var quote = Quote.New("Hunter Builders", "door hardware", 420_000, Today,
            new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10)));

        // Sent Tuesday, so three days on is Friday.
        QuoteVoice.Spoken(quote, Today).ShouldBe("$4,200 to Hunter Builders, chasing it Friday.");
    }
}
