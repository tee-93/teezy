using Shouldly;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Dashboard widgets staying the size they were left.</summary>
public sealed class DashboardSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Every_widget_starts_at_its_normal_size()
    {
        new TeezySettings().ExpandedSections.ShouldBeEmpty();
    }

    [Fact]
    public void Enlarged_widgets_survive_a_restart()
    {
        new TeezySettings { ExpandedSections = ["week", "day"] }.Save(_path);

        TeezySettings.Load(_path).ExpandedSections.ShouldBe(["week", "day"]);
    }

    [Fact]
    public void A_settings_file_from_before_widgets_could_grow_leaves_them_all_normal()
    {
        File.WriteAllText(_path, """{ "CleanupEnabled": true }""");

        TeezySettings.Load(_path).ExpandedSections.ShouldBeEmpty();
    }
}
