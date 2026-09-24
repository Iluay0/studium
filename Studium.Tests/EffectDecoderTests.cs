using Studium.Core.Combat;

namespace Studium.Tests;

public class EffectDecoderTests
{
    [Fact]
    public void PlainDamage()
    {
        Assert.True(EffectDecoder.TryDecode(3, 0, 0, 0, 0, 12345, out var e));
        Assert.Equal(new DecodedEffect(HitKind.Damage, 12345, false, false, false), e);
    }

    [Fact]
    public void DamageCritAndDirectHitComeFromParam0()
    {
        Assert.True(EffectDecoder.TryDecode(3, 0x60, 0, 0, 0, 100, out var e));
        Assert.True(e.Crit);
        Assert.True(e.DirectHit);
    }

    [Fact]
    public void ExtendedValueAddsParam3AsHighWord()
    {
        // 2 * 65536 + 1000
        Assert.True(EffectDecoder.TryDecode(3, 0, 0, 2, 0x40, 1000, out var e));
        Assert.Equal(132_072, e.Amount);
    }

    [Fact]
    public void Param3IsIgnoredWithoutExtendedFlag()
    {
        Assert.True(EffectDecoder.TryDecode(3, 0, 0, 2, 0, 1000, out var e));
        Assert.Equal(1000, e.Amount);
    }

    [Fact]
    public void HealCritComesFromParam1NotParam0()
    {
        Assert.True(EffectDecoder.TryDecode(4, 0x20, 0, 0, 0, 500, out var notCrit));
        Assert.False(notCrit.Crit);
        Assert.True(EffectDecoder.TryDecode(4, 0, 0x20, 0, 0, 500, out var crit));
        Assert.True(crit.Crit);
        Assert.Equal(HitKind.Heal, crit.Kind);
    }

    [Fact]
    public void SourceEntryFlagMarksEffectOnCaster()
    {
        Assert.True(EffectDecoder.TryDecode(4, 0, 0, 0, 0x80, 500, out var e));
        Assert.True(e.TargetsSource);
    }

    [Theory]
    [InlineData(5, HitKind.BlockedDamage)]
    [InlineData(6, HitKind.ParriedDamage)]
    [InlineData(1, HitKind.Miss)]
    [InlineData(7, HitKind.Invulnerable)]
    public void OtherKinds(byte type, HitKind kind)
    {
        Assert.True(EffectDecoder.TryDecode(type, 0, 0, 0, 0, 10, out var e));
        Assert.Equal(kind, e.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)] // apply status
    [InlineData(11)] // MP gain
    public void NonCombatEntriesAreIgnored(byte type) =>
        Assert.False(EffectDecoder.TryDecode(type, 0, 0, 0, 0, 10, out _));

    // Values taken from in-game captures on a striking dummy (2026-09-24).
    [Fact]
    public void DotTick()
    {
        Assert.True(EffectDecoder.TryDecodeTick(0x605, 1301, 0x1001FC92, out var isHeal, out var amount, out var source));
        Assert.False(isHeal);
        Assert.Equal(1301, amount);
        Assert.Equal(0x1001FC92u, source);
    }

    [Fact]
    public void HotTick()
    {
        Assert.True(EffectDecoder.TryDecodeTick(0x604, 5450, 0x1001FC92, out var isHeal, out var amount, out _));
        Assert.True(isHeal);
        Assert.Equal(5450, amount);
    }

    [Fact]
    public void TickNeedsAmountSourceAndTickCategory()
    {
        Assert.False(EffectDecoder.TryDecodeTick(0x605, 0, 0x1001FC92, out _, out _, out _));
        Assert.False(EffectDecoder.TryDecodeTick(0x605, 100, 0, out _, out _, out _));
        Assert.False(EffectDecoder.TryDecodeTick(0x605, 100, EffectDecoder.InvalidEntityId, out _, out _, out _));
        Assert.False(EffectDecoder.TryDecodeTick(0x17, 100, 0x1001FC92, out _, out _, out _));
    }
}
