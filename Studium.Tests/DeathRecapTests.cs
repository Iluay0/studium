using Studium.Core.Combat;
using Studium.Core.Fights;
using Studium.Core.History;

namespace Studium.Tests;

public class DeathRecapTests
{
    private const uint Me = 1, Healer = 2, Boss = 100;
    private const uint Regen = 158;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);

    private sealed class World : ICombatWorld
    {
        public bool IsAlly(uint id) => id is Me or Healer;

        public ActorSnapshot? Lookup(uint id) => id switch
        {
            Me => new("Iluay Dory", 23, 0),
            Healer => new("Luna Frost", 24, 0),
            Boss => new("Wicked Thunder", 0, 0),
            _ => null,
        };
    }

    private readonly FightTracker tracker = new(new World());

    private void BossHits(double seconds, long amount, uint action, uint hpBefore, bool crit = false) =>
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(seconds), Boss, 0, Me, action, 1, HitKind.Damage, amount, crit, false,
            Hp: new TargetHp(hpBefore, 100_000)));

    [Fact]
    public void DeathKeepsTheLastThirtySecondsNewestFirst()
    {
        BossHits(0, 10_000, 500, 100_000);   // 45 s before death: outside the window
        BossHits(40, 30_000, 501, 90_000);
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(42), Healer, 0, Me, 124, 1, HitKind.Heal, 20_000, true, false,
            Overheal: 0, Hp: new TargetHp(60_000, 100_000)));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(43), Healer, 0, Me, true, 5_000, StatusIds: [Regen],
            Hp: new TargetHp(80_000, 100_000)));
        BossHits(44, 90_000, 502, 85_000, crit: true);
        tracker.Handle(new DeathEvent(T0.AddSeconds(45), Me, Boss));

        var death = Assert.Single(tracker.Current!.Deaths);
        Assert.Equal("Iluay Dory", death.VictimName);
        Assert.Equal(45, death.SecondsIntoFight);
        Assert.Equal([1.0, 2.0, 3.0, 5.0], death.Events.Select(e => e.SecondsBeforeDeath));

        var killingBlow = death.KillingBlow!;
        Assert.Equal(502u, killingBlow.AbilityKey);
        Assert.Equal("Wicked Thunder", killingBlow.SourceName);
        Assert.True(killingBlow.Crit);
        Assert.Equal(0u, killingBlow.HpAfter);

        var heal = death.Events.Single(e => e.AbilityKey == 124);
        Assert.Equal(RecapKind.Heal, heal.Kind);
        Assert.Equal("Luna Frost", heal.SourceName);
        Assert.Equal(80_000u, heal.HpAfter);

        var regen = death.Events.Single(e => e.Kind == RecapKind.Heal && e.AbilityKey == AbilityStats.StatusKey(Regen));
        Assert.Equal(85_000u, regen.HpAfter);
    }

    [Fact]
    public void SecondDeathOnlySeesEventsSinceTheFirst()
    {
        BossHits(1, 100_000, 500, 100_000);
        tracker.Handle(new DeathEvent(T0.AddSeconds(2), Me, Boss));
        BossHits(5, 100_000, 501, 100_000);
        tracker.Handle(new DeathEvent(T0.AddSeconds(6), Me, Boss));

        Assert.Equal(2, tracker.Current!.Deaths.Count);
        Assert.Equal(501u, Assert.Single(tracker.Current.Deaths[1].Events).AbilityKey);
    }

    [Fact]
    public void UnknownHpLeavesHpAfterEmpty()
    {
        tracker.Handle(new ActionHitEvent(T0, Boss, 0, Me, 500, 1, HitKind.Damage, 1000, false, false));
        tracker.Handle(new DeathEvent(T0.AddSeconds(1), Me, Boss));
        Assert.Null(tracker.Current!.Deaths[0].Events[0].HpAfter);
    }

    [Fact]
    public void DeathsAreSavedWithTheFight()
    {
        var directory = Path.Combine(Path.GetTempPath(), "studium-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            BossHits(1, 100_000, 500, 100_000);
            tracker.Handle(new DeathEvent(T0.AddSeconds(2), Me, Boss));
            tracker.End(FightOutcome.Wipe, T0.AddSeconds(3));

            new FightStore(directory).Save(tracker.Last!);
            var loaded = new FightStore(directory).Load(tracker.Last!.Id)!;
            var death = Assert.Single(loaded.Deaths);
            Assert.Equal(500u, death.KillingBlow!.AbilityKey);
            Assert.Equal(0u, death.KillingBlow.HpAfter);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefensesAreKeptWithEachEventAndSaved()
    {
        var defense = new DefenseSnapshot(
            [new StatusSnapshot(1191, 0, "Iluay Dory"), new StatusSnapshot(638, 2, "Wicked Thunder")],
            [new StatusSnapshot(1193, 0, "Taro Brandt")],
            ShieldPercent: 25);
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(1), Boss, 0, Me, 500, 1, HitKind.Damage, 100_000, false, false,
            Hp: new TargetHp(100_000, 100_000), Defense: defense));
        tracker.Handle(new DeathEvent(T0.AddSeconds(2), Me, Boss));

        var recorded = tracker.Current!.Deaths[0].Events[0].Defense!;
        Assert.Equal(25, recorded.ShieldPercent);
        Assert.Equal([1191u, 638u], recorded.Target.Select(s => s.StatusId));
        Assert.Equal(2, recorded.Target[1].Stacks);
        Assert.Equal("Taro Brandt", Assert.Single(recorded.OnAttacker).SourceName);

        var directory = Path.Combine(Path.GetTempPath(), "studium-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            tracker.End(FightOutcome.Wipe, T0.AddSeconds(3));
            new FightStore(directory).Save(tracker.Last!);
            var loaded = new FightStore(directory).Load(tracker.Last!.Id)!;
            Assert.Equal(recorded, loaded.Deaths[0].Events[0].Defense, new DefenseComparer());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class DefenseComparer : IEqualityComparer<DefenseSnapshot?>
    {
        public bool Equals(DefenseSnapshot? a, DefenseSnapshot? b) =>
            a != null && b != null && a.ShieldPercent == b.ShieldPercent
            && a.Target.SequenceEqual(b.Target) && a.OnAttacker.SequenceEqual(b.OnAttacker);

        public int GetHashCode(DefenseSnapshot? obj) => obj?.ShieldPercent ?? 0;
    }

    [Fact]
    public void ShieldAbsorbsDamageBeforeHp()
    {
        // 100k max HP, 50k HP, 20% shield (20k). A 30k hit: 20k absorbed, 10k off HP, no shield left.
        var hp = new TargetHp(50_000, 100_000);
        var (hpAfter, shieldAfter) = DeathRecorder.After(new(RecapKind.Damage, 30_000, 0), hp, 20);
        Assert.Equal(40_000u, hpAfter);
        Assert.Equal(0, shieldAfter);

        // A 5k hit into the same shield: HP untouched, 15% shield left.
        (hpAfter, shieldAfter) = DeathRecorder.After(new(RecapKind.Damage, 5_000, 0), hp, 20);
        Assert.Equal(50_000u, hpAfter);
        Assert.Equal(15, shieldAfter);
    }

    [Fact]
    public void RecapUsesTheShieldForHpAfter()
    {
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(1), Boss, 0, Me, 500, 1, HitKind.Damage, 30_000, false, false,
            Hp: new TargetHp(50_000, 100_000), Defense: new DefenseSnapshot([], [], 20)));
        tracker.Handle(new DeathEvent(T0.AddSeconds(2), Me, Boss));
        var e = tracker.Current!.Deaths[0].Events[0];
        Assert.Equal(40_000u, e.HpAfter);
        Assert.Equal(0, e.ShieldAfterPercent);
    }

    [Fact]
    public void ShieldGainIsItsOwnLineWithExactValues()
    {
        const uint BrutalShell = 1997;
        tracker.Handle(new ActionHitEvent(T0, Boss, 0, Me, 500, 1, HitKind.Damage, 10_000, false, false, Hp: new TargetHp(100_000, 100_000)));
        tracker.Handle(new ShieldGainedEvent(T0.AddSeconds(1), Me, Me, BrutalShell, 0, 8, new TargetHp(90_000, 100_000)));
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(2), Boss, 0, Me, 501, 1, HitKind.Damage, 20_000, false, false,
            Hp: new TargetHp(90_000, 100_000), Defense: new DefenseSnapshot([], [], 8)));
        tracker.Handle(new DeathEvent(T0.AddSeconds(3), Me, Boss));

        var events = tracker.Current!.Deaths[0].Events;
        var shield = events.Single(e => e.Kind == RecapKind.Shield);
        Assert.Equal(AbilityStats.StatusKey(BrutalShell), shield.AbilityKey);
        Assert.Equal("Iluay Dory", shield.SourceName);
        Assert.Equal(8_000, shield.Amount);
        Assert.Equal(90_000u, shield.HpAfter);
        Assert.Equal(8, shield.ShieldAfterPercent);

        // The next hit eats the 8k shield first: 12k comes off HP.
        var hit = events.First();
        Assert.Equal(78_000u, hit.HpAfter);
        Assert.Equal(0, hit.ShieldAfterPercent);
    }

    [Fact]
    public void ShieldGainsOutsideAFightOrOnOthersAreIgnored()
    {
        tracker.Handle(new ShieldGainedEvent(T0, Me, Me, 1, 0, 10, new TargetHp(1, 1)));
        Assert.Null(tracker.Current);
    }
}
