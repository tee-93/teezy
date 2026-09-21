using System.Text.Json;

namespace Teezy.Core.Updates;

/// <summary>A published release, as far as updating is concerned.</summary>
/// <param name="Version">The version it installs, from the tag (<c>v1.12.1</c> → 1.12.1).</param>
/// <param name="Download">Where the installer is.</param>
/// <param name="Size">The installer's size in bytes, checked after download.</param>
/// <param name="Sha256">
/// The installer's SHA-256 as GitHub reports it, lower-case hex, or null when the release
/// predates GitHub publishing digests. When present, a download that does not match it is
/// never run.
/// </param>
public sealed record Release(Version Version, Uri Download, long Size, string? Sha256)
{
    /// <summary>The one file every release carries.</summary>
    public const string InstallerName = "TeezyFlow-Setup.exe";

    /// <summary>
    /// Reads GitHub's "latest release" response. Null when it is not something to install:
    /// a draft, a pre-release, a tag that is not a version, or a release without the installer.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because every one of those is an ordinary state of a
    /// repository and not a fault — the answer is simply "nothing to update to".
    /// </remarks>
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName) return null;
        if (!TryVersion(tagName, out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == InstallerName
                && asset.TryGetProperty("browser_download_url", out var url)
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var download)
                && download.Scheme == Uri.UriSchemeHttps)
            {
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                var sha = digest is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? value["sha256:".Length..].ToLowerInvariant()
                    : null;

                return new Release(version, download, size, sha);
            }
        }

        return null;
    }

    /// <summary><c>v1.12.1</c> or <c>1.12.1</c> → 1.12.1. Always three parts, so comparisons agree.</summary>
    public static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var trimmed = text.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOfAny(['+', '-']);
        if (plus >= 0) trimmed = trimmed[..plus];

        if (!Version.TryParse(trimmed, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Whether this release is newer than what is running.</summary>
    public bool IsNewerThan(Version running) =>
        Version > new Version(running.Major, running.Minor, Math.Max(running.Build, 0));

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
