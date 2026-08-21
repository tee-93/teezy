using Shouldly;
using Teezy.Core.Dictionary;
using Xunit;

namespace Teezy.Core.Tests;

public class DictionaryTests
{
    private static DictionaryCorrector Corrector(params DictionaryEntry[] entries) => new(entries);

    [Fact]
    public void LongestTriggerWins()
    {
        // "Claude Code" must be applied before "Claude", or the shorter rule rewrites the
        // first word and the longer one never gets to match.
        var c = Corrector(
            DictionaryEntry.Correction("cloud", "Claude"),
            DictionaryEntry.Correction("cloud code", "Claude Code"));

        c.Apply("open cloud code now").Text.ShouldBe("open Claude Code now");
    }

    [Theory]
    [InlineData("cloud code", "Claude Code")]
    [InlineData("CloudCode", "Claude Code")]
    [InlineData("cloud-code", "Claude Code")]
    [InlineData("Cloud  Code", "Claude Code")]
    public void MatchesGluedAndHyphenatedForms(string heard, string expected) =>
        Corrector(DictionaryEntry.Correction("cloud code", "Claude Code"))
            .Apply(heard).Text.ShouldBe(expected);

    [Theory]
    [InlineData("Cloudflare is down")]
    [InlineData("the cloud is fine")]
    [InlineData("cloudcoder")]
    public void NeverBitesIntoALongerWord(string input) =>
        Corrector(DictionaryEntry.Correction("cloud code", "Claude Code"))
            .Apply(input).Text.ShouldBe(input);

    [Fact]
    public void ReplacementIsLiteralNotASubstitutionPattern()
    {
        // "$1" in the user's own replacement text must be written out verbatim, not treated
        // as a regex group reference. This is a real hazard for arbitrary user input.
        var c = Corrector(DictionaryEntry.Correction("cost", "$1 million"));
        c.Apply("the cost").Text.ShouldBe("the $1 million");
    }

    [Fact]
    public void DisabledEntriesDoNothing() =>
        Corrector(DictionaryEntry.Correction("cloud", "Claude") with { IsEnabled = false })
            .Apply("cloud").Text.ShouldBe("cloud");

    [Fact]
    public void ReportsWhatWasActuallyHeardNotTheTrigger()
    {
        var (_, applied) = Corrector(DictionaryEntry.Correction("cloud code", "Claude Code"))
            .Apply("CloudCode and CloudCode");

        applied.Count.ShouldBe(1);
        applied[0].Heard.ShouldBe("CloudCode");   // not "cloud code"
        applied[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MatchesAcrossUnicodeNormalisationForms()
    {
        // "café" decomposed (e + combining acute) must match a composed trigger.
        var c = Corrector(DictionaryEntry.Correction("cafe\u0301 con leche", "café con leche"));
        c.Apply("one caf\u00e9 con leche").Text.ShouldBe("one café con leche");
    }

    [Fact]
    public void BiasPhrasesAreCappedAndDeduplicated()
    {
        var entries = Enumerable.Range(0, 100).Select(i => DictionaryEntry.Term($"word{i}"))
            .Append(DictionaryEntry.Term("WORD0"))
            .ToList();

        var phrases = DictionaryCorrector.BiasPhrases(entries);
        phrases.Count.ShouldBe(DictionaryCorrector.BiasLimit);
        phrases.ShouldBeUnique();
    }

    [Fact]
    public void ParsesTheFileFormat()
    {
        var entries = DictionaryStore.Parse(
        [
            "# a comment",
            "Anthropic",
            "cloud code -> Claude Code",
            "# off: teezy -> Teezy",
            "",
        ]);

        entries.Count.ShouldBe(3);
        entries[0].Kind.ShouldBe(EntryKind.Term);
        entries[0].Write.ShouldBe("Anthropic");
        entries[1].Kind.ShouldBe(EntryKind.Correction);
        entries[1].Hear.ShouldBe("cloud code");
        entries[2].IsEnabled.ShouldBeFalse();
    }
}
