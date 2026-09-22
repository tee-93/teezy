using Shouldly;
using Teezy.Core.Voice;
using Xunit;

namespace Teezy.Core.Tests;

public class KokoroVoicesTests
{
    [Fact]
    public void EveryVoiceHasItsOwnSpeaker()
    {
        KokoroVoices.All.Select(v => v.Id).ShouldBeUnique();
        KokoroVoices.All.Select(v => v.Speaker).ShouldBeUnique();
    }

    [Fact]
    public void TheNameSaysTheAccentAndGender()
    {
        // Kokoro's own naming: b = British, a = American; f = female, m = male.
        foreach (var voice in KokoroVoices.All)
        {
            voice.British.ShouldBe(voice.Id[0] == 'b', voice.Id);
            voice.Female.ShouldBe(voice.Id[1] == 'f', voice.Id);
        }
    }

    [Fact]
    public void SpeakerNumbersMatchTheModel()
    {
        // Read from the model's own speaker_names metadata, 2026-09-22.
        KokoroVoices.Find("bf_emma").Speaker.ShouldBe(21);
        KokoroVoices.Find("bm_george").Speaker.ShouldBe(26);
        KokoroVoices.Find("af_heart").Speaker.ShouldBe(3);
    }

    [Fact]
    public void AnUnknownOrUnsetVoiceIsTheBritishDefault()
    {
        KokoroVoices.Find(null).ShouldBe(KokoroVoices.Default);
        KokoroVoices.Find("zf_xiaobei").British.ShouldBeTrue();
    }
}
