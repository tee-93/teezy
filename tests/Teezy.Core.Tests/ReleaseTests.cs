using Shouldly;
using Teezy.Core.Updates;
using Xunit;

namespace Teezy.Core.Tests;

public class ReleaseTests
{
    private static string Json(string tag = "v1.13.0", bool draft = false, bool pre = false,
        string asset = "TeezyFlow-Setup.exe", string? digest = "sha256:ABCDEF") => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(pre ? "true" : "false")}},
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://example.com/notes.txt" },
            { "name": "{{asset}}", "size": 163732143,
              {{(digest is null ? "" : $"\"digest\": \"{digest}\",")}}
              "browser_download_url": "https://github.com/tee-93/teezy/releases/download/{{tag}}/{{asset}}" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheInstallerFromTheLatestRelease()
    {
        var release = Release.Parse(Json())!;

        release.Version.ShouldBe(new Version(1, 13, 0));
        release.Size.ShouldBe(163732143);
        release.Sha256.ShouldBe("abcdef");
        release.Download.AbsoluteUri.ShouldEndWith("/v1.13.0/TeezyFlow-Setup.exe");
    }

    [Fact]
    public void AReleaseWithoutADigestCanStillBeUsed() =>
        // Releases from before GitHub published digests. The size check still applies.
        Release.Parse(Json(digest: null))!.Sha256.ShouldBeNull();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DraftsAndPreReleasesAreNeverOffered(bool draft, bool pre) =>
        Release.Parse(Json(draft: draft, pre: pre)).ShouldBeNull();

    [Fact]
    public void AReleaseWithoutTheInstallerIsNothingToUpdateTo() =>
        Release.Parse(Json(asset: "Teezy-Setup.exe")).ShouldBeNull();

    [Fact]
    public void ATagThatIsNotAVersionIsIgnored() =>
        Release.Parse(Json(tag: "latest-build")).ShouldBeNull();

    [Theory]
    [InlineData("1.13.0", "1.12.1", true)]
    [InlineData("1.12.1", "1.12.1", false)]
    [InlineData("1.12.0", "1.12.1", false)]
    [InlineData("1.12.10", "1.12.9", true)]
    public void ComparesAsVersionsNotText(string latest, string running, bool newer)
    {
        Release.TryVersion(latest, out var l).ShouldBeTrue();
        Release.TryVersion(running, out var r).ShouldBeTrue();
        new Release(l, new Uri("https://example.com/x"), 1, null).IsNewerThan(r).ShouldBe(newer);
    }

    [Fact]
    public void TheRunningVersionsBuildSuffixDoesNotMatter()
    {
        // The informational version carries the commit: 1.12.1+c938169…
        Release.TryVersion("1.12.1+c93816967060", out var v).ShouldBeTrue();
        v.ShouldBe(new Version(1, 12, 1));
    }

    [Fact]
    public void AFourPartAssemblyVersionComparesEqualToItsTag() =>
        // Assembly versions are 1.12.1.0; the tag is v1.12.1. They are the same release.
        new Release(new Version(1, 12, 1), new Uri("https://example.com/x"), 1, null)
            .IsNewerThan(new Version(1, 12, 1, 0)).ShouldBeFalse();
}
