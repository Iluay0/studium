using Studium.Core;

namespace Studium.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("Iluay Dory", false, NameDisplay.Full, false, "Iluay Dory")]
    [InlineData("Iluay Dory", false, NameDisplay.Initials, false, "I. D.")]
    [InlineData("Iluay Dory", false, NameDisplay.SurnameInitial, false, "Iluay D.")]
    [InlineData("Eos", false, NameDisplay.Initials, false, "Eos")]
    [InlineData("Iluay Dory", true, NameDisplay.Initials, true, "YOU")]
    [InlineData("Luna Frost", false, NameDisplay.Full, true, "Luna Frost")]
    [InlineData("Iluay Dory", true, NameDisplay.SurnameInitial, false, "Iluay D.")]
    public void Names(string name, bool isSelf, NameDisplay display, bool youForSelf, string expected) =>
        Assert.Equal(expected, NameFormatter.Format(name, isSelf, display, youForSelf));

    [Fact]
    public void JobsHaveColoursAndIcons()
    {
        Assert.Equal("PCT", Jobs.Abbreviation(42));
        Assert.Equal(62142u, Jobs.IconId(42));
        Assert.Equal(Jobs.Rgb(23), Jobs.Rgb(5)); // archer shares bard's colour
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
