namespace Teezy.Core.Abstractions;

/// <summary>Stores a secret the app needs but must not keep in plain text.</summary>
/// <remarks>
/// An API key deliberately does not live in <c>settings.json</c>. That file is plain text, is
/// opened in an editor by anyone debugging, and is the sort of thing that ends up pasted into
/// a bug report. The platform implementation encrypts it for the current user instead.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Reads and decrypts, or null if absent or unreadable.</summary>
    string? Read(string name);

    void Write(string name, string secret);

    void Delete(string name);

    /// <summary>
    /// A masked rendering of the stored secret, or null if there is nothing usable stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced a <c>Has</c> that only asked whether a file existed. That could not tell
    /// "a key is saved" from "a file is sitting there that will not decrypt", so the settings
    /// page could cheerfully report a working key while cleanup silently fell back to the
    /// offline rules. Producing a mask forces the secret to be read and decrypted, so the UI
    /// claiming a key is saved is now a claim that it can actually be used.
    /// </para>
    /// <para>
    /// The masking happens here rather than in the caller, so the plaintext never leaves the
    /// store.
    /// </para>
    /// </remarks>
    string? Describe(string name) =>
        Read(name) is { Length: > 0 } secret ? SecretMask.For(secret) : null;
}

/// <summary>Renders a secret as something recognisable but not reusable.</summary>
public static class SecretMask
{
    /// <summary>
    /// Keeps the leading label and the last four characters — enough to confirm the key you
    /// just pasted is the one that landed, and to tell two keys apart, which is the same
    /// convention every API console and card statement uses.
    /// </summary>
    /// <remarks>
    /// Anything too short to be a real key is masked completely rather than half-shown: for a
    /// short secret, "first seven and last four" is most of it.
    /// </remarks>
    public static string For(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        return secret.Length < 20 ? new string('•', 10) : $"{secret[..7]}…{secret[^4..]}";
    }
}

/// <summary>Keeps a secret only for the life of the process.</summary>
/// <remarks>
/// Used where no platform store exists. Deliberately not a file: writing a key to disk
/// unencrypted because the real implementation is missing is worse than not persisting it.
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public string? Read(string name) => _secrets.GetValueOrDefault(name);
    public void Write(string name, string secret) => _secrets[name] = secret;
    public void Delete(string name) => _secrets.Remove(name);
}
