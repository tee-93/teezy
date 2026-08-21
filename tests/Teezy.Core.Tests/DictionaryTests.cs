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
    public void TheFileHeaderParsesToNothing() =>
        // The header is rewritten on every save and includes a line containing "# off:",
        // which is the marker for a disabled entry. If that parsed, every save would add a
        // phantom rule and the dictionary would grow junk forever.
        DictionaryStore.Parse(DictionaryStore.SampleFile).ShouldBeEmpty();

    [Fact]
    public void SavingThenLoadingKeepsExactlyTheEntriesGiven()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-dict-{Guid.NewGuid():N}.txt");
        var store = new DictionaryStore(path);

        store.Save(
        [
            DictionaryEntry.Correction("cloud code", "Claude Code"),
            DictionaryEntry.Term("Parakeet"),
            DictionaryEntry.Correction("teezy", "Teezy") with { IsEnabled = false },
        ]);

        store.Reload();
        store.Entries.Count.ShouldBe(3);
        store.Entries.Count(e => e.Kind == EntryKind.Correction).ShouldBe(2);
        store.Entries.ShouldContain(e => !e.IsEnabled);

        File.Delete(path);
    }

    [Fact]
    public void AccentedEntriesSurviveTheFile()
    {
        // Written without a BOM, Windows editors and shells fall back to the ANSI code page
        // and mangle these - then round-trip the mangled bytes back in on the next load.
        var path = Path.Combine(Path.GetTempPath(), $"teezy-dict-{Guid.NewGuid():N}.txt");
        var store = new DictionaryStore(path);

        store.Save(
        [
            DictionaryEntry.Correction("cafe con leche", "café con leche"),
            DictionaryEntry.Term("Björn Rundgren"),
            DictionaryEntry.Correction("zoe", "Zoë"),
        ]);

        store.Reload();
        store.Entries.Count.ShouldBe(3);

        // And the corrector built from the reloaded file still fires on them.
        store.Corrector.Apply("one cafe con leche").Text.ShouldBe("one café con leche");

        File.ReadAllBytes(path).Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });

        File.Delete(path);
    }

    [Fact]
    public void RepeatedSavesDoNotAccumulateHeaderJunk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-dict-{Guid.NewGuid():N}.txt");
        var store = new DictionaryStore(path);

        for (var i = 0; i < 5; i++)
        {
            store.Reload();
            store.Save([.. store.Entries, DictionaryEntry.Term($"word{i}")]);
        }

        store.Reload();
        store.Entries.Count.ShouldBe(5);

        File.Delete(path);
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

public class DictionaryWarningTests
{
    [Fact]
    public void WarnsWhenTheTriggerIsAnOrdinaryWord()
    {
        // "code -> Claude Code" would rewrite every spoken "code" in every sentence.
        var warnings = DictionaryWarning.Check(DictionaryEntry.Correction("code", "Claude Code"));
        warnings.ShouldContain(w => w.Severity == WarningSeverity.Caution);
    }

    [Fact]
    public void DoesNotWarnAboutAMultiWordTrigger() =>
        DictionaryWarning.Check(DictionaryEntry.Correction("cloud code", "Claude Code")).ShouldBeEmpty();

    [Fact]
    public void WarnsAboutVeryShortTriggers() =>
        DictionaryWarning.Check(DictionaryEntry.Correction("qa", "QA"))
            .ShouldContain(w => w.Severity == WarningSeverity.Caution);

    [Fact]
    public void FlagsARuleThatRewritesToItself() =>
        DictionaryWarning.Check(DictionaryEntry.Correction("Anthropic", "Anthropic"))
            .ShouldContain(w => w.Severity == WarningSeverity.Useless);

    [Fact]
    public void TermsAreNeverWarnedAbout() =>
        // A Term only biases the engine; it is never matched against text, so it cannot
        // misfire no matter how ordinary the word is.
        DictionaryWarning.Check(DictionaryEntry.Term("code")).ShouldBeEmpty();

    [Fact]
    public void CapitalisationFixesAreNotFlaggedAsUseless() =>
        // The most common reason to add an entry at all: the model hears the word correctly
        // but writes it lower-case.
        DictionaryWarning.Check(DictionaryEntry.Correction("kubernetes", "Kubernetes")).ShouldBeEmpty();

    [Fact]
    public void UncommonSingleWordsAreFine() =>
        DictionaryWarning.Check(DictionaryEntry.Correction("terraform", "Terraform")).ShouldBeEmpty();

    [Fact]
    public void EmptyTriggerProducesNothingRatherThanThrowing() =>
        DictionaryWarning.Check(DictionaryEntry.Correction("  ", "x")).ShouldBeEmpty();
}
