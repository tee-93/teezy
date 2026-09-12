using Shouldly;
using Teezy.Core.Commands;
using Xunit;

namespace Teezy.Core.Tests;

public class CommandMatcherTests
{
    // ---- normalising what the recogniser wrote ----

    [Theory]
    [InlineData("Open Chrome.", "open chrome")]
    [InlineData("  VOLUME   UP  ", "volume up")]
    [InlineData("Set the volume to 40%.", "set the volume to 40")]
    [InlineData("Lock — my computer!", "lock my computer")]
    public void PunctuationAndCaseAreStrippedBeforeMatching(string spoken, string expected) =>
        // Parakeet writes "Open Chrome." with a capital and a full stop, and every pattern
        // would otherwise have to carry that noise.
        CommandMatcher.Normalise(spoken).ShouldBe(expected);

    // ---- launching ----

    [Theory]
    [InlineData("open Chrome", "chrome")]
    [InlineData("launch Visual Studio", "visual studio")]
    [InlineData("start Spotify", "spotify")]
    [InlineData("switch to Outlook", "outlook")]
    [InlineData("go to Teams", "teams")]
    public void LaunchTakesWhateverWasNamed(string spoken, string app) =>
        // The name is passed through unresolved: what is installed is the platform's business,
        // not Core's.
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.LaunchApp(app));

    [Fact]
    public void LaunchWithNothingToLaunchIsNotACommand() =>
        CommandMatcher.Match("open").ShouldBeNull();

    // ---- volume ----

    [Theory]
    [InlineData("volume up")]
    [InlineData("turn it up")]
    [InlineData("louder")]
    [InlineData("increase the volume")]
    public void VolumeGoesUpByAStep(string spoken) =>
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.AdjustVolume(CommandMatcher.VolumeStep));

    [Theory]
    [InlineData("volume down")]
    [InlineData("turn it down")]
    [InlineData("quieter")]
    public void VolumeGoesDownByAStep(string spoken) =>
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.AdjustVolume(-CommandMatcher.VolumeStep));

    [Theory]
    [InlineData("set the volume to 40", 40)]
    [InlineData("set the volume to forty", 40)]
    [InlineData("volume to forty five percent", 45)]
    [InlineData("set volume to one hundred", 100)]
    [InlineData("turn the volume to zero", 0)]
    public void VolumeCanBeSetInWordsOrDigits(string spoken, int percent) =>
        // Both spellings for the same utterance: the recogniser is not consistent about which
        // it writes, and a matcher that knew only one would look broken at random.
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.SetVolume(percent));

    [Fact]
    public void AnImpossibleVolumeIsClamped() =>
        CommandMatcher.Match("set the volume to 350").ShouldBe(new VoiceCommand.SetVolume(100));

    [Fact]
    public void AVolumeThatIsNotANumberIsNotACommand() =>
        // "I didn't understand" beats setting the volume to an arbitrary number.
        CommandMatcher.Match("set the volume to whatever").ShouldBeNull();

    [Theory]
    [InlineData("mute", true)]
    [InlineData("mute the sound", true)]
    [InlineData("unmute", false)]
    [InlineData("turn the sound back on", false)]
    public void MutingIsBothWays(string spoken, bool on) =>
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.Mute(on));

    // ---- media ----

    [Theory]
    [InlineData("play", MediaKey.PlayPause)]
    [InlineData("pause", MediaKey.PlayPause)]
    [InlineData("next", MediaKey.Next)]
    [InlineData("next track", MediaKey.Next)]
    [InlineData("skip", MediaKey.Next)]
    [InlineData("previous song", MediaKey.Previous)]
    public void TransportKeys(string spoken, MediaKey key) =>
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.Media(key));

    // ---- lock ----

    [Theory]
    [InlineData("lock")]
    [InlineData("lock the computer")]
    [InlineData("lock my screen")]
    public void Locking(string spoken) =>
        CommandMatcher.Match(spoken).ShouldBe(new VoiceCommand.LockScreen());

    // ---- not commands ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("what is the weather in London")]
    [InlineData("remind me to call the dentist")]
    [InlineData("close all my windows")]
    public void AnythingElseMatchesNothing(string spoken) =>
        // Including "close all my windows": deliberately not in the vocabulary, because
        // misheard speech plus an irreversible action is the pairing to avoid.
        CommandMatcher.Match(spoken).ShouldBeNull();

    [Fact]
    public void ASentenceContainingACommandWordIsNotACommand() =>
        // Every pattern is anchored at both ends. An unanchored "mute" would fire on dictation
        // the user meant for a text box.
        CommandMatcher.Match("I need to mute this later").ShouldBeNull();

    // ---- spoken numbers ----

    [Theory]
    [InlineData("40", 40)]
    [InlineData("forty", 40)]
    [InlineData("forty five", 45)]
    [InlineData("forty-five", 45)]
    [InlineData("fourty", 40)]
    [InlineData("a hundred", 100)]
    [InlineData("one hundred", 100)]
    [InlineData("seven", 7)]
    [InlineData("nineteen", 19)]
    public void NumbersInWordsOrDigits(string spoken, int expected) =>
        // "fourty" is in there because the recogniser spells it that way often enough to matter.
        SpokenNumber.Parse(spoken).ShouldBe(expected);

    [Theory]
    [InlineData("maximum")]
    [InlineData("the max")]
    [InlineData("")]
    [InlineData("loud")]
    public void WhatIsNotANumberReturnsNothing(string spoken) =>
        SpokenNumber.Parse(spoken).ShouldBeNull();
}
