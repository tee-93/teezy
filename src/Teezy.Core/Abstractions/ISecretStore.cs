namespace Teezy.Core.Abstractions;

/// <summary>Stores a secret the app needs but must not keep in plain text.</summary>
/// <remarks>
/// An API key deliberately does not live in <c>settings.json</c>. That file is plain text, is
/// opened in an editor by anyone debugging, and is the sort of thing that ends up pasted into
/// a bug report. The platform implementation encrypts it for the current user instead.
/// </remarks>
public interface ISecretStore
{
    /// <summary>True if a secret is stored, without decrypting it.</summary>
    bool Has(string name);

    /// <summary>Reads and decrypts, or null if absent or unreadable.</summary>
    string? Read(string name);

    void Write(string name, string secret);

    void Delete(string name);
}

/// <summary>Keeps a secret only for the life of the process.</summary>
/// <remarks>
/// Used where no platform store exists. Deliberately not a file: writing a key to disk
/// unencrypted because the real implementation is missing is worse than not persisting it.
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public bool Has(string name) => _secrets.ContainsKey(name);
    public string? Read(string name) => _secrets.GetValueOrDefault(name);
    public void Write(string name, string secret) => _secrets[name] = secret;
    public void Delete(string name) => _secrets.Remove(name);
}
