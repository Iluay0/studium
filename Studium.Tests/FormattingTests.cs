using Studium.Core;

namespace Studium.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("Alma Sylphe", false, NameDisplay.Full, false, "Alma Sylphe")]
    [InlineData("Alma Sylphe", false, NameDisplay.Initials, false, "A. S.")]
    [InlineData("Alma Sylphe", false, NameDisplay.SurnameInitial, false, "Alma S.")]
    [InlineData("Eos", false, NameDisplay.Initials, false, "Eos")]
    [InlineData("Alma Sylphe", true, NameDisplay.Initials, true, "YOU")]
    [InlineData("Luna Frost", false, NameDisplay.Full, true, "Luna Frost")]
    [InlineData("Alma Sylphe", true, NameDisplay.SurnameInitial, false, "Alma S.")]
    public void Names(string name, bool isSelf, NameDisplay display, bool youForSelf, string expected) =>
        Assert.Equal(expected, NameFormatter.Format(name, isSelf, display, youForSelf));

    [Fact]
    public void JobsHaveColoursAndIcons()
    {
        Assert.Equal("PCT", Jobs.Abbreviation(42));
        Assert.Equal(62142u, Jobs.IconId(42));
        Assert.Equal(Jobs.Rgb(23), Jobs.Rgb(5));
        Assert.Equal(Jobs.FallbackRgb, Jobs.Rgb(0));
        Assert.Equal(0u, Jobs.IconId(0));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(9_999, "9,999")]
    [InlineData(39_300, "39.3k")]
    [InlineData(14_840_000, "14.84M")]
    public void Compact(long value, string expected) =>
        Assert.Equal(expected, Studium.Core.Format.Compact(value));

    [Fact]
    public void Durations()
    {
        Assert.Equal("08:42", Studium.Core.Format.Duration(new TimeSpan(0, 8, 42)));
        Assert.Equal("1:02:03", Studium.Core.Format.Duration(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void Percent() => Assert.Equal("32%", Studium.Core.Format.Percent(0.3249));
}

public class FflogsLinksTests
{
    [Fact]
    public void CharacterUrl() =>
        Assert.Equal("https://www.fflogs.com/character/eu/moogle/Alma%20Sylphe",
            Studium.Core.FflogsLinks.CharacterUrl(3, "Moogle", "Alma Sylphe"));

    [Theory]
    [InlineData(1u, "jp")]
    [InlineData(2u, "na")]
    [InlineData(4u, "oc")]
    [InlineData(7u, "na")]
    public void Regions(uint id, string slug) => Assert.Equal(slug, Studium.Core.FflogsLinks.RegionSlug(id));

    [Fact]
    public void UnknownRegionOrMissingDataGivesNoUrl()
    {
        Assert.Null(Studium.Core.FflogsLinks.CharacterUrl(0, "Moogle", "Alma Sylphe"));
        Assert.Null(Studium.Core.FflogsLinks.CharacterUrl(3, "", "Alma Sylphe"));
    }
}

public class MeterVisibilityTests
{
    [Theory]
    [InlineData(Studium.Core.MeterVisibility.Always, false, false, false, true)]
    [InlineData(Studium.Core.MeterVisibility.InCombat, false, true, false, false)]
    [InlineData(Studium.Core.MeterVisibility.InCombat, true, false, false, true)]
    [InlineData(Studium.Core.MeterVisibility.InCombat, false, false, true, true)]
    [InlineData(Studium.Core.MeterVisibility.InDuty, true, false, true, false)]
    [InlineData(Studium.Core.MeterVisibility.InDuty, false, true, false, true)]
    public void Rules(Studium.Core.MeterVisibility visibility, bool inCombat, bool inDuty, bool fightRunning, bool expected) =>
        Assert.Equal(expected, Studium.Core.MeterVisibilityRules.ShouldShow(visibility, inCombat, inDuty, fightRunning));
}
