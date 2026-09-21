using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teezy.Core.Sync;

/// <summary>The passphrase was wrong, or the file is not a TeezyFlow sync file.</summary>
public sealed class SyncUnlockException(string message) : Exception(message);

/// <summary>Seals and opens the sync file with a passphrase.</summary>
/// <remarks>
/// <para>
/// <b>The file holds API keys and sits in a cloud folder</b>, so it is encrypted with a key
/// only the passphrase can produce: PBKDF2-SHA256 at 600,000 iterations (OWASP's current
/// figure for that function) over a fresh random salt, then AES-256-GCM, which also detects
/// any change to the file. Without the passphrase the file is noise — to OneDrive, to anyone
/// who copies it, and to anyone who finds it on a lost laptop.
/// </para>
/// <para>
/// Not DPAPI, which is what the keys use on each machine: DPAPI ties a secret to one Windows
/// account on one computer, which is exactly what a file meant for three computers cannot be.
/// </para>
/// </remarks>
public static class SyncCipher
{
    public const string Format = "teezyflow-sync";
    private const int Iterations = 600_000;

    public static string Seal(string plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Derive(passphrase, salt, Iterations);

        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, data, cipher, tag, Header);
        CryptographicOperations.ZeroMemory(key);

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["kdf"] = "pbkdf2-sha256",
            ["iterations"] = Iterations,
            ["salt"] = Convert.ToBase64String(salt),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag),
            ["data"] = Convert.ToBase64String(cipher),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <exception cref="SyncUnlockException">Wrong passphrase, or not a sync file.</exception>
    public static string Open(string sealedText, string passphrase)
    {
        try
        {
            var root = JsonNode.Parse(sealedText)!.AsObject();
            if ((string?)root["format"] != Format || (int?)root["version"] != 1)
            {
                throw new SyncUnlockException("That isn’t a TeezyFlow sync file.");
            }

            var salt = Convert.FromBase64String((string)root["salt"]!);
            var nonce = Convert.FromBase64String((string)root["nonce"]!);
            var tag = Convert.FromBase64String((string)root["tag"]!);
            var cipher = Convert.FromBase64String((string)root["data"]!);
            var iterations = (int?)root["iterations"] ?? Iterations;

            var key = Derive(passphrase, salt, iterations);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, tag.Length);
                aes.Decrypt(nonce, cipher, tag, plain, Header);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new SyncUnlockException("That passphrase doesn’t open the sync file.");
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                                      or NullReferenceException or ArgumentException)
        {
            throw new SyncUnlockException("That isn’t a TeezyFlow sync file.");
        }
    }

    /// <summary>Bound into the encryption, so the header cannot be swapped onto other data.</summary>
    private static ReadOnlySpan<byte> Header => "teezyflow-sync/1"u8;

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
}
