using Shouldly;
using Teezy.Core.Quotes;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Reading a quote from one line, typed at the desk or said in the car.</summary>
public class QuoteInputTests
{
    // Tuesday 22 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static ParsedQuote Parse(string line) => QuoteInput.Parse(line, Today);

    [Fact]
    public void TheCustomerComesBeforeTheFigureAndTheJobAfterIt()
    {
        var quote = Parse("Hunter Builders $4,200 door hardware");

        quote.Customer.ShouldBe("Hunter Builders");
        quote.AmountCents.ShouldBe(420_000);
        quote.What.ShouldBe("door hardware");
        quote.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public void CentsAreKept()
    {
        Parse("Orikan $4,207.35 readers").AmountCents.ShouldBe(420_735);
    }

    [Fact]
    public void AFigureWithNoDollarSignStillReads()
    {
        Parse("Hunter Builders 4200 door hardware").AmountCents.ShouldBe(420_000);
    }

    [Fact]
    public void FourPointTwoKIsFourThousandTwoHundred()
    {
        Parse("Hunter Builders 4.2k door hardware").AmountCents.ShouldBe(420_000);
    }

    [Fact]
    public void ADayAtTheEndIsWhenItWentOut()
    {
        var quote = Parse("Hunter Builders $4,200 door hardware sent friday");

        quote.Sent.ShouldBe(new DateOnly(2026, 9, 18));
        quote.What.ShouldBe("door hardware");
    }

    [Fact]
    public void ADayThatHasNotHappenedYetIsTheOneJustGone()
    {
        // A quote is sent before it is recorded, so "25 Aug" on 22 September 2026 is this
        // August, not next — and "friday" is the Friday behind us.
        Parse("Orikan $900 readers sent 25 aug").Sent.ShouldBe(new DateOnly(2026, 8, 25));
        Parse("Orikan $900 readers sent friday").Sent.ShouldBe(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public void WithNoDayItIsForTheCallerToDecide()
    {
        Parse("Hunter Builders $4,200 door hardware").Sent.ShouldBeNull();
    }

    [Fact]
    public void AQuoteNumberIsPickedOutWhereverItSits()
    {
        Parse("Hunter Builders $4,200 door hardware #Q1042").Reference.ShouldBe("Q1042");
        Parse("Orikan ref 8831 $900 readers").Reference.ShouldBe("8831");
    }

    [Fact]
    public void LeadingWordsPeopleActuallyTypeAreDropped()
    {
        Parse("quoted Hunter Builders $4,200 for door hardware").Customer.ShouldBe("Hunter Builders");
        Parse("quote for Orikan $900 readers").Customer.ShouldBe("Orikan");
    }

    [Fact]
    public void SpokenMoneyIsUnderstood()
    {
        var quote = Parse("quoted Hunter Builders four thousand two hundred for door hardware");

        quote.Customer.ShouldBe("Hunter Builders");
        quote.AmountCents.ShouldBe(420_000);
        quote.What.ShouldBe("door hardware");
    }

    [Theory]
    [InlineData("nine hundred", 90_000)]
    [InlineData("twelve thousand", 1_200_000)]
    [InlineData("four thousand two hundred and fifty", 425_000)]
    [InlineData("one hundred and twenty thousand", 12_000_000)]
    public void SpokenFiguresAddUp(string said, long cents)
    {
        Parse($"quoted Orikan {said} for readers").AmountCents.ShouldBe(cents);
    }

    [Fact]
    public void AYearInTheJobIsNotMistakenForThePrice()
    {
        Parse("Hunter Builders $4,200 stage 2 door hardware").AmountCents.ShouldBe(420_000);
    }

    [Fact]
    public void ALineWithNoFigureIsNotUsable()
    {
        var quote = Parse("Hunter Builders door hardware");

        quote.AmountCents.ShouldBe(0);
        quote.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void NothingTypedIsNotUsableEither()
    {
        Parse("   ").IsUsable.ShouldBeFalse();
    }
}
