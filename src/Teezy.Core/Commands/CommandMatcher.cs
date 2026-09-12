using System.Text;
using System.Text.RegularExpressions;

namespace Teezy.Core.Commands;

/// <summary>
/// Turns what was said into one of the things Teezy can do, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not natural language understanding.</b> The vocabulary is a dozen verbs and
/// one free parameter, and an ordered list of patterns covers it completely — with the large
/// advantages of being instant, working offline, costing nothing, and failing in ways a person
/// can predict. When an utterance matches nothing the answer is "I can't do that", not a guess.
/// </para>
/// <para>
/// Ordering matters and is not alphabetical. The launch patterns come last because they take a
/// free argument and would otherwise swallow phrases the specific rules should have claimed.
/// </para>
/// <para>
/// In <c>Teezy.Core</c> for the usual reason: this is where the behaviour worth testing lives,
/// and it needs neither a microphone nor a desktop to test.
/// </para>
/// </remarks>
public static partial class CommandMatcher
{
    /// <summary>How far one "volume up" moves it.</summary>
    /// <remarks>
    /// Ten points, so three presses is a third of the range. Windows' own volume keys move two,
    /// which is fine when you can tap them quickly and far too slow when each press costs a
    /// sentence.
    /// </remarks>
    public const int VolumeStep = 10;

    /// <summary>What was actually understood, for matching and for showing the user.</summary>
    /// <remarks>
    /// Lower-cased, stripped of punctuation and with runs of whitespace collapsed. The
    /// recogniser writes "Open Chrome." with a capital and a full stop, and every pattern here
    /// would have to carry that noise otherwise.
    /// </remarks>
    public static string Normalise(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return string.Empty;

        var builder = new StringBuilder(spoken.Length);
        var lastWasSpace = true;

        foreach (var c in spoken)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The command this utterance asks for, or null if it asks for nothing we do.</summary>
    public static VoiceCommand? Match(string? spoken)
    {
        var text = Normalise(spoken);
        if (text.Length == 0) return null;

        if (Lock().IsMatch(text)) return new VoiceCommand.LockScreen();

        if (Unmute().IsMatch(text)) return new VoiceCommand.Mute(false);
        if (Mute().IsMatch(text)) return new VoiceCommand.Mute(true);

        if (SetVolume().Match(text) is { Success: true } set)
        {
            // A number we cannot read is a misrecognition, not a command. Falling through to
            // "I didn't understand" is better than setting the volume to something arbitrary.
            if (SpokenNumber.Parse(set.Groups["n"].Value) is not { } percent) return null;
            return new VoiceCommand.SetVolume(Math.Clamp(percent, 0, 100));
        }

        if (VolumeUp().IsMatch(text)) return new VoiceCommand.AdjustVolume(VolumeStep);
        if (VolumeDown().IsMatch(text)) return new VoiceCommand.AdjustVolume(-VolumeStep);

        if (PlayPause().IsMatch(text)) return new VoiceCommand.Media(MediaKey.PlayPause);
        if (NextTrack().IsMatch(text)) return new VoiceCommand.Media(MediaKey.Next);
        if (PreviousTrack().IsMatch(text)) return new VoiceCommand.Media(MediaKey.Previous);

        // Last: it takes a free argument and would claim phrases the rules above should own.
        if (Launch().Match(text) is { Success: true } launch)
        {
            var app = launch.Groups["app"].Value.Trim();
            return app.Length == 0 ? null : new VoiceCommand.LaunchApp(app);
        }

        return null;
    }

    // Anchored at both ends on purpose. An unanchored "mute" would fire on "I need to mute
    // this later", which is dictation someone meant for a text box.

    [GeneratedRegex(@"^lock( (the|my))?( pc| computer| screen| machine| laptop)?$")]
    private static partial Regex Lock();

    [GeneratedRegex(@"^(unmute|unmute (the )?(volume|audio|sound)|turn (the )?(sound|audio|volume) back on)$")]
    private static partial Regex Unmute();

    [GeneratedRegex(@"^(mute|mute (the )?(volume|audio|sound)|silence)$")]
    private static partial Regex Mute();

    [GeneratedRegex(@"^(set |turn |change |put )?(the )?volume (to|at) (?<n>.+?)( per ?cent)?$")]
    private static partial Regex SetVolume();

    [GeneratedRegex(@"^(volume up|turn (it |the volume )?up|louder|increase (the )?volume|turn up the volume)$")]
    private static partial Regex VolumeUp();

    [GeneratedRegex(@"^(volume down|turn (it |the volume )?down|quieter|decrease (the )?volume|turn down the volume)$")]
    private static partial Regex VolumeDown();

    [GeneratedRegex(@"^(play|pause|play pause|resume|unpause)$")]
    private static partial Regex PlayPause();

    [GeneratedRegex(@"^(next|next (track|song)|skip( (this|the)? ?(track|song))?)$")]
    private static partial Regex NextTrack();

    [GeneratedRegex(@"^(previous|previous (track|song)|last (track|song)|go back a (track|song))$")]
    private static partial Regex PreviousTrack();

    [GeneratedRegex(@"^(open|launch|start|run|switch to|go to) (?<app>.+)$")]
    private static partial Regex Launch();
}
