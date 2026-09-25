using Studium.Core.Combat;

namespace Studium.Tests;

/// <summary>Tooltips in macro form, copied from the game's English ActionTransient sheet (colour macros trimmed in places).</summary>
public class TooltipPotencyTests
{
    private const uint Bard = 23;
    private const uint WhiteMage = 24;
    private const uint Dragoon = 22;
    private const uint Archer = 5;

    private const string Dia =
        "Deals unaspected damage with a potency of <if([gnum68==24],<if([gnum72>=94],85,65)>,65)>.<br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>Unaspected damage over time<br><colortype(504)><edgecolortype(505)>Potency: <edgecolortype(0)><colortype(0)><if([gnum68==24],<if([gnum72>=94],85,65)>,65)><br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>30s";

    private const string Stormbite =
        "Deals wind damage with a potency of 100.<br><colortype(504)><edgecolortype(505)>Additional Effect:<edgecolortype(0)><colortype(0)> Wind damage over time<br><colortype(504)><edgecolortype(505)>Potency:<edgecolortype(0)><colortype(0)> 25<br><colortype(504)><edgecolortype(505)>Duration:<edgecolortype(0)><colortype(0)> 45s<if([gnum68==23],<if([gnum72>=76],<br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>35% chance of granting <colortype(506)><edgecolortype(507)>Hawk's Eye<edgecolortype(0)><colortype(0)><br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>30s,)>,)>";

    private const string VenomousBite =
        "Delivers an attack with a potency of 100.<br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>Venom<br><colortype(504)><edgecolortype(505)>Potency: <edgecolortype(0)><colortype(0)>15<br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>45s";

    private const string HeavyShot =
        "Delivers an attack with a potency of 160.<if([gnum72>=2],<if([gnum68==5],<br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>20% chance of granting <colortype(506)><edgecolortype(507)>Hawk's Eye<edgecolortype(0)><colortype(0)><br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>30s,<if([gnum68==23],<br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>20% chance of granting <colortype(506)><edgecolortype(507)>Hawk's Eye<edgecolortype(0)><colortype(0)><br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>30s,)>)>,)>";

    private const string ChaoticSpring =
        "Delivers an attack with a potency of <if([gnum68==22],<if([gnum72>=94],140,100)>,100)>.<br><if([gnum68==22],<if([gnum72>=94],180,140)>,140)> when executed from a target's rear.<br><colortype(504)><edgecolortype(505)>Combo Potency: <edgecolortype(0)><colortype(0)><if([gnum68==22],<if([gnum72>=94],300,260)>,260)><br><colortype(504)><edgecolortype(505)>Rear Combo Potency: <edgecolortype(0)><colortype(0)><if([gnum68==22],<if([gnum72>=94],340,300)>,300)><br><colortype(504)><edgecolortype(505)>Combo Bonus: <edgecolortype(0)><colortype(0)>Damage over time<br><colortype(504)><edgecolortype(505)>Potency: <edgecolortype(0)><colortype(0)>45<br><colortype(504)><edgecolortype(505)>Duration: <edgecolortype(0)><colortype(0)>24s";

    [Theory]
    [InlineData(100, 85)]
    [InlineData(94, 85)]
    [InlineData(93, 65)] // before the level 94 trait
    public void LevelPicksTheBranch(int level, int expected)
    {
        var potency = TooltipPotency.Read(Dia, WhiteMage, level);
        Assert.Equal(expected, potency.Potency);
        Assert.Equal(expected, potency.DotPotency);
    }

    [Fact]
    public void OtherJobsGetTheBasePotency() =>
        Assert.Equal(65, TooltipPotency.Read(Dia, Bard, 100).Potency);

    [Fact]
    public void DotLineAfterColourMacros()
    {
        var potency = TooltipPotency.Read(Stormbite, Bard, 100);
        Assert.Equal(new ActionPotency(100, 25, true), potency);
    }

    [Fact]
    public void DotWithoutDamageOverTimeWording() =>
        Assert.Equal(new ActionPotency(100, 15, true), TooltipPotency.Read(VenomousBite, Bard, 60));

    [Fact]
    public void PlainHitIsFixed() =>
        Assert.Equal(new ActionPotency(160, null, true), TooltipPotency.Read(HeavyShot, Archer, 30));

    [Fact]
    public void CombosAndPositionalsAreNotFixed()
    {
        var potency = TooltipPotency.Read(ChaoticSpring, Dragoon, 100);
        Assert.Equal(140, potency.Potency);
        Assert.Equal(45, potency.DotPotency);
        Assert.False(potency.IsFixed);
    }

    [Fact]
    public void EvaluatesToTheTooltipText() =>
        Assert.Equal("Delivers an attack with a potency of 160.\nAdditional Effect: 20% chance of granting Hawk's Eye\nDuration: 30s",
            TooltipPotency.Evaluate(HeavyShot, Bard, 100));

    [Fact]
    public void EscapedCommasStayText() =>
        Assert.Equal("Yields, crystal, and cluster",
            TooltipPotency.Evaluate(@"Yields<if([gnum68>=16],\, crystal\, and cluster,)>", 16, 50));

    [Fact]
    public void ThousandsSeparator() =>
        Assert.Equal(1200, TooltipPotency.Read(@"Delivers an attack with a potency of 1\,200.", Bard, 100).Potency);

    [Fact]
    public void NoPotency() =>
        Assert.Equal(new ActionPotency(null, null, false), TooltipPotency.Read("Increases movement speed.", Bard, 100));

    private const string MedicaII =
        "Restores own HP and the HP of all nearby party members.<br>Cure Potency: <if([gnum68==24],<if([gnum72>=85],250,200)>,200)><br><colortype(504)><edgecolortype(505)>Additional Effect: <edgecolortype(0)><colortype(0)>Regen<br>Cure Potency: <if([gnum68==24],<if([gnum72>=85],150,100)>,100)><br>Duration: 15s";

    [Fact]
    public void HealWithRegen()
    {
        var potency = TooltipPotency.Read(MedicaII, WhiteMage, 100);
        Assert.Equal(250, potency.HealPotency);
        Assert.Equal(150, potency.HotPotency);
        Assert.True(potency.IsHealFixed);
        Assert.Null(potency.Potency);
    }

    [Fact]
    public void HealOverTimeOnly()
    {
        var potency = TooltipPotency.Read("Grants healing over time effect to target.<br>Cure Potency: 250<br>Duration: 18s", WhiteMage, 100);
        Assert.Null(potency.HealPotency);
        Assert.Equal(250, potency.HotPotency);
    }

    [Fact]
    public void PlainHeal() =>
        Assert.Equal(new ActionPotency(null, null, false, 800, null, true),
            TooltipPotency.Read("Restores target's HP.<br>Cure Potency: 800", WhiteMage, 100));
}
