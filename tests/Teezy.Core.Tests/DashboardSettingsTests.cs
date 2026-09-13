using Shouldly;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Dashboard sections staying the way they were left.</summary>
public sealed class DashboardSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Every_section_starts_open()
    {
        new TeezySettings().ClosedSections.ShouldBeEmpty();
    }

    [Fact]
    public void Closed_sections_survive_a_restart()
    {
        new TeezySettings { ClosedSections = ["week", "inbox"] }.Save(_path);

        TeezySettings.Load(_path).ClosedSections.ShouldBe(["week", "inbox"]);
    }

    [Fact]
    public void A_settings_file_from_before_sections_could_close_leaves_them_all_open()
    {
        File.WriteAllText(_path, """{ "CleanupEnabled": true }""");

        TeezySettings.Load(_path).ClosedSections.ShouldBeEmpty();
    }
}
