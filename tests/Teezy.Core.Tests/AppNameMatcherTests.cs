using Shouldly;
using Teezy.Core.Commands;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Choosing which installed application someone meant.</summary>
public class AppNameMatcherTests
{
    private static readonly string[] Installed =
    [
        "Google Chrome", "Google Chrome Canary", "Outlook", "Outlook (classic)",
        "Microsoft Teams", "TeamViewer", "Visual Studio Insiders", "Spotify",
        "Notepad", "Steam",
    ];

    [Theory]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("google chrome", "Google Chrome")]
    [InlineData("spotify", "Spotify")]
    [InlineData("notepad", "Notepad")]
    public void FindsTheObviousOne(string spoken, string expected) =>
        AppNameMatcher.Best(spoken, Installed).ShouldBe(expected);

    [Fact]
    public void PrefersThePlainThingOverTheVariant() =>
        // Someone saying a short name almost always means the plain one. "Chrome" is Chrome,
        // not Chrome Canary.
        AppNameMatcher.Best("chrome", Installed).ShouldBe("Google Chrome");

    [Fact]
    public void AnExactNameBeatsAWholeWordMatch() =>
        // "Outlook" is installed under that exact name and also as "Outlook (classic)".
        AppNameMatcher.Best("outlook", Installed).ShouldBe("Outlook");

    [Fact]
    public void AWholeWordBeatsAFragment() =>
        // "teams" is a word in "Microsoft Teams" and merely a fragment of nothing else.
        // TeamViewer must not win this — launching the wrong application is the failure that
        // matters, and it is why nothing here is fuzzy.
        AppNameMatcher.Best("teams", Installed).ShouldBe("Microsoft Teams");

    [Fact]
    public void ShortFragmentsAreNotEnough() =>
        // Two letters inside a name is a coincidence, not a request.
        AppNameMatcher.Best("ea", Installed).ShouldBeNull();

    [Fact]
    public void SomethingNotInstalledFindsNothing() =>
        AppNameMatcher.Best("photoshop", Installed).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingSpokenFindsNothing(string? spoken) =>
        AppNameMatcher.Best(spoken, Installed).ShouldBeNull();

    [Fact]
    public void CaseDoesNotMatter() =>
        AppNameMatcher.Best("SPOTIFY", Installed).ShouldBe("Spotify");

    [Fact]
    public void AnEmptyMachineFindsNothing() =>
        AppNameMatcher.Best("chrome", []).ShouldBeNull();
}
