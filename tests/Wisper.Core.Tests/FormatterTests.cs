using Shouldly;
using Wisper.Core.Formatting;
using Xunit;

namespace Wisper.Core.Tests;

public class FormatterTests
{
    private static string Format(string input) =>
        new RuleBasedFormatter().FormatAsync(input).GetAwaiter().GetResult();

    [Theory]
    [InlineData("um so I was thinking", "So I was thinking.")]
    [InlineData("Uh, the build is green", "The build is green.")]
    [InlineData("it works, um, mostly", "It works, mostly.")]
    public void StripsFillers(string input, string expected) =>
        Format(input).ShouldBe(expected);

    [Fact]
    public void DoesNotEatRealWordsThatStartLikeFillers() =>
        // "umbrella" and "uhh-oh" style words must survive: a cleanup pass that removes
        // meaning is worse than one that leaves a stray filler behind.
        Format("The umbrella is huge").ShouldBe("The umbrella is huge.");

    [Fact]
    public void LeavesLikeAndYouKnowAlone() =>
        Format("a list like this one, you know").ShouldBe("A list like this one, you know.");

    [Theory]
    [InlineData("send it new line thanks", "Send it\nThanks.")]
    [InlineData("done new paragraph next up", "Done\n\nNext up.")]
    public void AppliesSpokenPunctuation(string input, string expected) =>
        Format(input).ShouldBe(expected);

    [Fact]
    public void ScratchThatDropsOnlyTheCurrentSentence() =>
        // The first sentence must survive; only the clause carrying the command goes.
        Format("The build is green. Deploy on Friday scratch that. Deploy Monday.")
            .ShouldBe("The build is green. Deploy Monday.");

    [Fact]
    public void CollapsesWhitespaceAndFixesSpaceBeforePunctuation() =>
        Format("hello   there , friend").ShouldBe("Hello there, friend.");

    [Theory]
    [InlineData("already punctuated.", "Already punctuated.")]
    [InlineData("a question?", "A question?")]
    [InlineData("no terminal mark", "No terminal mark.")]
    public void AddsTerminalPunctuationOnlyWhenAbsent(string input, string expected) =>
        Format(input).ShouldBe(expected);

    [Fact]
    public void IsIdempotent()
    {
        // Parakeet already emits punctuated, sentence-cased text, so every rule here has to
        // be safe to apply to output that is already clean.
        const string input = "um, so the deploy failed. we should retry";
        var once = Format(input);
        Format(once).ShouldBe(once);
    }

    [Fact]
    public void EmptyInputStaysEmpty() => Format("   ").ShouldBe("");
}
