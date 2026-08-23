using Shouldly;
using Teezy.Core.Speech;
using Xunit;

namespace Teezy.Core.Tests;

public class HotwordEncoderTests
{
    // A stand-in for the Parakeet vocabulary: SentencePiece pieces with ▁ marking a word
    // start, plus single characters as the fallback path.
    private static readonly HashSet<string> Vocab =
    [
        "▁ph", "▁p", "▁a", "▁an", "▁the", "oe", "be", "th", "ro", "pic",
        "a", "b", "c", "e", "h", "i", "n", "o", "p", "r", "t", "▁", "▁z",
    ];

    [Fact]
    public void SegmentsAWordIntoPiecesTheVocabularyActuallyHas() =>
        // The whole point: sherpa-onnx looks each piece up in tokens.txt, so "Phoebe" as a
        // single token fails to encode and the hint is silently skipped.
        HotwordEncoder.Segment("phoebe", Vocab).ShouldBe("▁ph oe be");

    [Fact]
    public void PrefersTheLongestPieceAtEachStep() =>
        // Shortest-first would give "▁p h oe be" — a valid path, but not one the decoder
        // sees in real output, so it biases toward nothing useful.
        HotwordEncoder.Segment("phoebe", Vocab).ShouldStartWith("▁ph");

    [Fact]
    public void FallsBackToLowercaseWhenTheWrittenFormWillNotEncode() =>
        // Hints are written the way they should be spelled — "Phoebe", not "phoebe" — and
        // this vocabulary has no capitals, so the lowercase attempt is what saves them.
        HotwordEncoder.Segment("Phoebe", Vocab).ShouldBe("▁ph oe be");

    [Fact]
    public void ReturnsNullForAWordTheVocabularyCannotExpress() =>
        // "q" is in neither the pieces nor the single characters. Null so the caller drops
        // the hint, rather than emitting a token sherpa-onnx will reject at load.
        HotwordEncoder.Segment("qqq", Vocab).ShouldBeNull();

    [Fact]
    public void SegmentsEachWordOfAMultiWordHintFromItsOwnStart() =>
        HotwordEncoder.Segment("the phoebe", Vocab).ShouldBe("▁the ▁ph oe be");

    [Fact]
    public void EncodeDropsOnlyTheHintsItCannotHandle()
    {
        var encoded = HotwordEncoder.Encode(["phoebe", "qqq", "the"], Vocab);

        // One bad hint must not cost the good ones — the old behaviour skipped every word.
        encoded.ShouldBe("▁ph oe be\n▁the");
    }

    [Fact]
    public void EncodeReturnsNullWhenNothingSurvives() =>
        HotwordEncoder.Encode(["qqq", "zzzq"], Vocab).ShouldBeNull();

    [Fact]
    public void EncodeReturnsNullForNoHintsAtAll() =>
        HotwordEncoder.Encode([], Vocab).ShouldBeNull();

    [Fact]
    public void BlankHintsAreIgnored() =>
        HotwordEncoder.Segment("   ", Vocab).ShouldBeNull();

    [Fact]
    public void VocabularyParsingKeepsTheTokenAndDropsTheId()
    {
        var file = Path.GetTempFileName();
        try
        {
            // Note the third line: the token is a space. Splitting on the first space would
            // lose it; splitting on the last is why it survives.
            File.WriteAllLines(file, ["<unk> 0", "▁th 1", "  2"]);

            var vocab = HotwordEncoder.LoadVocabulary(file);

            vocab.ShouldContain("<unk>");
            vocab.ShouldContain("▁th");
            vocab.ShouldContain(" ");
            vocab.ShouldNotContain("1");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
