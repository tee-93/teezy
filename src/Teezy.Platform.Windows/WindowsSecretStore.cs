using System.Security.Cryptography;
using System.Text;
using Teezy.Core.Abstractions;

namespace Teezy.Platform.Windows;

/// <summary>Stores secrets encrypted for the current Windows user, via DPAPI.</summary>
/// <remarks>
/// <para>
/// <see cref="DataProtectionScope.CurrentUser"/>: the ciphertext is bound to this user
/// account on this machine. Copying the file to another machine, or reading it as another
/// user, yields nothing — which is the property that makes it safe to leave in
/// <c>%LOCALAPPDATA%</c> next to the settings.
/// </para>
/// <para>
/// Not a full secret manager, and not pretending to be: an attacker already running code as
/// this user can decrypt it, exactly as they could read any credential this user can use.
/// What it does buy is that the key never appears in plain text on disk, in a settings file
/// someone opens in an editor, or in a bug report.
/// </para>
/// </remarks>
public sealed class WindowsSecretStore : ISecretStore
{
    /// <summary>Bound into the ciphertext, so a file swapped in from elsewhere fails to
    /// decrypt rather than silently yielding another secret.</summary>
    private static readonly byte[] Entropy = "Teezy.SecretStore.v1"u8.ToArray();

    private readonly string _directory;

    public WindowsSecretStore(string? directory = null) =>
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Teezy", "secrets");

    private string PathFor(string name) => Path.Combine(_directory, $"{name}.bin");

    public bool Has(string name) => File.Exists(PathFor(name));

    public string? Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException)
        {
            // Wrong user, wrong machine, or a corrupt file. Treated as "no secret" rather
            // than as an error: the caller's job is to ask for it again, not to crash.
            return null;
        }
    }

    public void Write(string name, string secret)
    {
        Directory.CreateDirectory(_directory);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), cipher);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
