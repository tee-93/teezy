namespace Teezy.Core.Calendar;

/// <summary>Which side of your life an account belongs to.</summary>
/// <remarks>
/// Anticipated rather than used yet. Teezy will eventually answer differently depending on
/// which mode you are in — your work diary is not what you want read out at the weekend, and
/// your personal one is not what you want on a shared screen in the office. Modelling it now
/// costs a field; retrofitting it would mean revisiting every account, every answer and every
/// stored token.
/// </remarks>
public enum CalendarProfile
{
    Personal,
    Work,
}

/// <summary>A calendar account the user has signed in to.</summary>
/// <param name="Id">
/// Teezy's own identifier for this connection, and the key its tokens are filed under. Not the
/// provider's account id and not the email address: the same address can be connected twice —
/// personal Microsoft and work Microsoft are routinely the same human — and an address is also
/// the kind of thing one would rather not write into a settings file.
/// </param>
/// <param name="DisplayName">What to call it in Settings. The email address, usually.</param>
/// <param name="Source">Which provider.</param>
/// <param name="Profile">Work or personal.</param>
/// <remarks>
/// A list, deliberately, rather than one field per provider. Two Microsoft accounts is the
/// normal case for anyone with a job, and a shape that allows only one would have to be
/// unpicked the first time that mattered.
/// </remarks>
public sealed record ConnectedAccount(
    string Id,
    string DisplayName,
    CalendarSource Source,
    CalendarProfile Profile)
{
    /// <summary>A fresh identifier for a newly connected account.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
