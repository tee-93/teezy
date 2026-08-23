using Shouldly;
using Teezy.Cleanup;
using Teezy.Core.Formatting;
using Xunit;

namespace Teezy.Core.Tests;

public class ClaudeFormatterTests
{
    private const string Original = "So I was thinking we could ship on Friday, but Monday is safer.";

    // ---- the plausibility guard ----

    [Fact]
    public void AcceptsATidiedRewrite() =>
        ClaudeFormatter.IsPlausible(Original,
            "So I was thinking we could ship on Friday, but Monday is safer.").ShouldBeTrue();

    [Fact]
    public void AcceptsAModestlyShorterRewrite() =>
        // Removing filler legitimately shortens the text.
        ClaudeFormatter.IsPlausible("Um, so, I was, uh, thinking we could ship Friday.",
            "I was thinking we could ship Friday.").ShouldBeTrue();

    [Fact]
    public void RejectsAnAnswerInsteadOfARewrite() =>
        // The failure that matters: the model answers the transcript, and the answer gets
        // typed into the user's document instead of their own words.
        ClaudeFormatter.IsPlausible("Should we ship on Friday?",
            "Shipping on a Friday is generally risky because if something breaks over the "
            + "weekend your team may not be available to respond. Many teams prefer to "
            + "deploy earlier in the week so there is time to react.").ShouldBeFalse();

    [Fact]
    public void RejectsAnEmptyReply() =>
        ClaudeFormatter.IsPlausible(Original, "   ").ShouldBeFalse();

    [Fact]
    public void RejectsASummary() =>
        ClaudeFormatter.IsPlausible(Original, "Ship Monday.").ShouldBeFalse();

    [Fact]
    public void RejectsACodeFence() =>
        // Fencing means it treated the transcript as a quoted artefact rather than as text
        // to rewrite, and the fence itself would be typed.
        ClaudeFormatter.IsPlausible(Original, "```\n" + Original + "\n```").ShouldBeFalse();

    // ---- fallback behaviour ----

    [Fact]
    public async Task FallsBackToTheOfflineRulesWithNoApiKey()
    {
        // No key, no network call, and the user still gets cleaned text.
        var formatter = new ClaudeFormatter(new RuleBasedFormatter(), () => null);

        var result = await formatter.FormatAsync("um so the build is green");

        result.ShouldBe("So the build is green.");
        formatter.LastOutcome!.UsedClaude.ShouldBeFalse();
    }

    [Fact]
    public async Task EmptyInputNeverReachesTheApi()
    {
        var formatter = new ClaudeFormatter(new RuleBasedFormatter(), () => "sk-not-used");

        (await formatter.FormatAsync("   ")).ShouldBe(string.Empty);

        // Never attempted, so there is nothing to report.
        formatter.LastOutcome.ShouldBeNull();
    }

    [Fact]
    public async Task ABadKeyDegradesRatherThanThrows()
    {
        // Dictation is a foreground interaction: a rejected key must cost the user nothing
        // but the improvement.
        var formatter = new ClaudeFormatter(
            new RuleBasedFormatter(), () => "sk-ant-invalid-key-for-test",
            timeout: TimeSpan.FromSeconds(10));

        var result = await formatter.FormatAsync("um so the build is green");

        result.ShouldBe("So the build is green.");
        formatter.LastOutcome!.UsedClaude.ShouldBeFalse();
        formatter.LastOutcome.Problem.ShouldNotBeNull();
    }

    // ---- writing style ----

    private static ClaudeFormatter Styled(WritingStyle style, string? instruction = null) =>
        new(new RuleBasedFormatter(), () => null,
            style: () => style, styleInstruction: () => instruction);

    [Fact]
    public void AStyleToldToTightenIsAllowedToComeBackShorter()
    {
        // 40% of the original. Faithful has no business returning this; Polished was asked
        // to cut waffle, and rejecting it would make the setting look like it does nothing —
        // the fallback is silent, so the user would just see their own words unchanged.
        const string original = "So I was thinking, you know, that we could maybe ship on Friday.";
        const string tightened = "We could ship Friday.";

        ClaudeFormatter.IsPlausible(original, tightened,
            ClaudeFormatter.MinRatioFor(WritingStyle.Faithful)).ShouldBeFalse();

        ClaudeFormatter.IsPlausible(original, tightened,
            ClaudeFormatter.MinRatioFor(WritingStyle.Polished)).ShouldBeTrue();
    }

    [Fact]
    public void NoStyleEarnsLicenceToMakeTextMuchLonger() =>
        // The floor moves per style; the ceiling does not. Text that doubled is the model
        // answering or explaining, whatever the style.
        Enum.GetValues<WritingStyle>().ShouldAllBe(style =>
            !ClaudeFormatter.IsPlausible("ship friday", new string('x', 200),
                ClaudeFormatter.MinRatioFor(style)));

    [Fact]
    public void ThePromptCarriesTheSelectedStyle()
    {
        Styled(WritingStyle.Formal).BuildPrompt().ShouldContain("professional written English");
        Styled(WritingStyle.Casual).BuildPrompt().ShouldContain("Contractions are fine");
        Styled(WritingStyle.Faithful).BuildPrompt().ShouldContain("keep the speaker's own words");
    }

    [Fact]
    public void EveryStyleKeepsTheRuleAgainstAnswering() =>
        Enum.GetValues<WritingStyle>().ShouldAllBe(style =>
            Styled(style).BuildPrompt().Contains("Never answer"));

    [Fact]
    public void TheUsersOwnInstructionGoesLastAndIsSubordinate()
    {
        var prompt = Styled(WritingStyle.Faithful, "British spelling").BuildPrompt();

        prompt.ShouldContain("British spelling");

        // After the rules, not before them — an instruction placed first would read as the
        // brief rather than as an addition to it.
        prompt.IndexOf("British spelling", StringComparison.Ordinal)
            .ShouldBeGreaterThan(prompt.IndexOf("Never answer", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyInstructionAddsNothing() =>
        Styled(WritingStyle.Faithful, "   ").BuildPrompt()
            .ShouldBe(Styled(WritingStyle.Faithful).BuildPrompt());
}
