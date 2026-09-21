namespace Teezy.App;

/// <summary>TeezyFlow's own app registrations, built in so a new computer needs none typed.</summary>
/// <remarks>
/// <para>
/// <b>These are identifiers, not secrets,</b> which is the only reason they can live in a public
/// repository and a public installer. A client id is shown on every sign-in page and is designed
/// to be embedded in desktop apps; it lets TeezyFlow ask to be signed in to, and nothing more.
/// What protects the account is the person signing in and the consent they give.
/// </para>
/// <para>
/// The Microsoft registration is "Teezy" in the PURSIVA COM AU tenant, audience "All Microsoft
/// account users", redirect <c>http://localhost</c>. The Google one is the Desktop client in
/// project teezy-508405. Google also issues a client secret for desktop clients; it is not
/// really secret either, but it is kept out of the code all the same and travels by sync.
/// </para>
/// <para>A value typed into Settings still wins, for anyone using their own registration.</para>
/// </remarks>
internal static class BuiltInApps
{
    public const string MicrosoftClientId = "1a789a44-5f45-49c0-8733-218c5e6850a1";

    public const string GoogleClientId = "813537581014-vjcde6j1b1gs48jppqdimfpepeg47n9b.apps.googleusercontent.com";
}
