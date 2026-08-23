using Shouldly;
using Teezy.Core;
using Teezy.Core.Formatting;
using Xunit;

namespace Teezy.Core.Tests;

public class AppRuleTests
{
    private static TeezySettings With(params AppRule[] rules) => new()
    {
        WritingStyle = WritingStyle.Faithful,
        StyleInstruction = "global line",
        AppRules = rules,
    };

    // ---- matching ----

    [Fact]
    public void MatchesIgnoringCase() =>
        // Windows reports Outlook as "OUTLOOK". Nobody should have to know that to write a
        // rule for it.
        new AppRule { App = "outlook" }.Matches("OUTLOOK").ShouldBeTrue();

    [Fact]
    public void MatchesWithOrWithoutTheExtension() =>
        new AppRule { App = "Teams.exe" }.Matches("teams").ShouldBeTrue();

    [Fact]
    public void DoesNotMatchOnSubstrings() =>
        // The deliberate limit. Substring matching would make a rule for "code" quietly
        // capture "vscode" — behaviour nobody can predict from the list they are looking at.
        new AppRule { App = "code" }.Matches("vscode").ShouldBeFalse();

    [Fact]
    public void MatchesNothingWhenTheAppIsUnknown() =>
        // Dictating into something with no reportable process is not a match for every rule.
        new AppRule { App = "outlook" }.Matches(null).ShouldBeFalse();

    // ---- resolution ----

    [Fact]
    public void FallsBackToTheGlobalStyleWhenNoRuleMatches()
    {
        var style = With(new AppRule { App = "outlook", Style = WritingStyle.Formal })
            .StyleFor("notepad");

        style.ShouldBe(new CleanupStyle(WritingStyle.Faithful, "global line"));
    }

    [Fact]
    public void AMatchingRuleWins() =>
        With(new AppRule { App = "outlook", Style = WritingStyle.Formal, Instruction = "sign off politely" })
            .StyleFor("OUTLOOK")
            .ShouldBe(new CleanupStyle(WritingStyle.Formal, "sign off politely"));

    [Fact]
    public void ARuleReplacesTheGlobalInstructionRatherThanAddingToIt() =>
        // Two instructions arriving together is how you get contradictory ones.
        With(new AppRule { App = "cursor", Style = WritingStyle.Faithful })
            .StyleFor("cursor")
            .Instruction.ShouldBeNull();

    [Fact]
    public void FirstMatchWins() =>
        // The order shown on the settings page is the order applied, so a rule can be
        // shadowed by one above it — visibly, rather than by some hidden precedence.
        With(
            new AppRule { App = "slack", Style = WritingStyle.Casual },
            new AppRule { App = "slack", Style = WritingStyle.Formal })
            .StyleFor("slack")
            .Style.ShouldBe(WritingStyle.Casual);

    [Fact]
    public void ADisabledRuleIsSkippedButDoesNotBlockTheNextOne() =>
        With(
            new AppRule { App = "slack", Style = WritingStyle.Formal, Enabled = false },
            new AppRule { App = "slack", Style = WritingStyle.Casual })
            .StyleFor("slack")
            .Style.ShouldBe(WritingStyle.Casual);

    [Fact]
    public void NoRulesAtAllIsJustTheGlobalStyle() =>
        With().StyleFor("anything").ShouldBe(new CleanupStyle(WritingStyle.Faithful, "global line"));

    [Fact]
    public void AnUnknownAppGetsTheGlobalStyle() =>
        With(new AppRule { App = "outlook", Style = WritingStyle.Formal })
            .StyleFor(null)
            .Style.ShouldBe(WritingStyle.Faithful);
}
