using Echo.Core;

namespace Echo.Tests;

public class RetentionPeriodTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(168, 168)]
    [InlineData(9999, 720)]
    public void Clamp_KeepsHoursWithinZeroToOneMonth(int input, int expected) =>
        Assert.Equal(expected, RetentionPeriod.Clamp(input));

    [Fact]
    public void DisplayMax_IsThirtyDaysOr720Hours()
    {
        Assert.Equal(30, RetentionPeriod.DisplayMax(RetentionUnit.Days));
        Assert.Equal(720, RetentionPeriod.DisplayMax(RetentionUnit.Hours));
    }

    [Theory]
    [InlineData(168, RetentionUnit.Days, 7)]
    [InlineData(168, RetentionUnit.Hours, 168)]
    [InlineData(36, RetentionUnit.Days, 2)]
    [InlineData(35, RetentionUnit.Days, 1)]
    [InlineData(0, RetentionUnit.Days, 0)]
    public void ToDisplay_ConvertsStoredHours(int hours, RetentionUnit unit, int expected) =>
        Assert.Equal(expected, RetentionPeriod.ToDisplay(hours, unit));

    [Theory]
    [InlineData(7, RetentionUnit.Days, 168)]
    [InlineData(30, RetentionUnit.Days, 720)]
    [InlineData(31, RetentionUnit.Days, 720)]
    [InlineData(5, RetentionUnit.Hours, 5)]
    public void FromDisplay_ConvertsBackToHours(int value, RetentionUnit unit, int expected) =>
        Assert.Equal(expected, RetentionPeriod.FromDisplay(value, unit));

    [Theory]
    [InlineData(0, "This session only (nothing saved to disk)")]
    [InlineData(1, "1 hour")]
    [InlineData(12, "12 hours")]
    [InlineData(24, "1 day")]
    [InlineData(168, "7 days")]
    [InlineData(36, "36 hours (1.5 days)")]
    public void Describe_IsHumanReadable(int hours, string expected) =>
        Assert.Equal(expected, RetentionPeriod.Describe(hours));

    [Fact]
    public void ZeroMeansSessionOnly()
    {
        Assert.True(RetentionPeriod.IsSessionOnly(0));
        Assert.False(RetentionPeriod.IsSessionOnly(1));
    }
}
