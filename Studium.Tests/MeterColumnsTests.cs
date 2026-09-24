using Studium.Core;

namespace Studium.Tests;

public class MeterColumnsTests
{
    [Fact]
    public void DefaultsMatchTheOriginalLayout()
    {
        Assert.Equal(["DPS", "D%", "Total", "Crit", "DH", "Max hit", "Deaths"], MeterColumns.Resolve(null, MeterTab.Dps).Select(c => c.Header));
        Assert.Equal(["Taken", "T%", "Parry", "Block", "Healed-on", "Deaths"], MeterColumns.Resolve(null, MeterTab.Tank).Select(c => c.Header));
        Assert.Equal(["HPS", "H%", "Total", "Heal", "Shield", "Overheal", "Heal crit", "Deaths"], MeterColumns.Resolve(null, MeterTab.Heal).Select(c => c.Header));
    }

    [Fact]
    public void ConfiguredOrderIsKeptAndJunkDropped()
    {
        var columns = MeterColumns.Resolve(["deaths", "nope", "dps", "deaths", "misses"], MeterTab.Dps);
        Assert.Equal(["deaths", "dps", "misses"], columns.Select(c => c.Id));
    }

    [Fact]
    public void ColumnsFromOtherTabsAreIgnored()
    {
        var columns = MeterColumns.Resolve(["dps", "taken", "hps"], MeterTab.Dps);
        Assert.Equal(["dps"], columns.Select(c => c.Id));
    }

    [Fact]
    public void DefaultsAreAvailableOnTheirTab()
    {
        foreach (var tab in Enum.GetValues<MeterTab>())
            Assert.All(MeterColumns.Defaults(tab), id => Assert.Contains(id, MeterColumns.Available(tab)));
    }

    [Fact]
    public void NothingUsableFallsBackToDefaults()
    {
        Assert.Equal(MeterColumns.Defaults(MeterTab.Heal), MeterColumns.Resolve(["nope"], MeterTab.Heal).Select(c => c.Id));
        Assert.Equal(MeterColumns.Defaults(MeterTab.Heal), MeterColumns.Resolve([], MeterTab.Heal).Select(c => c.Id));
    }

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(MeterColumns.All.Count, MeterColumns.All.Select(c => c.Id).Distinct().Count());
}
