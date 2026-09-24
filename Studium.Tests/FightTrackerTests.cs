using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Tests;

public class FightTrackerTests
{
    private const uint Me = 1, Healer = 2, MyPet = 50, Boss = 100, Add = 101;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);

    private sealed class FakeWorld : ICombatWorld
    {
        public bool IsAlly(uint id) => id is Me or Healer;

        public ActorSnapshot? Lookup(uint id) => id switch
        {
            Me => new("Iluay Dory", 38, 0),
            Healer => new("Luna F", 24, 0),
            MyPet => new("Eos", 0, Me),
            Boss => new("Striking Dummy", 0, 0),
            Add => new("Add", 0, 0),
            _ => null,
        };
    }

    private readonly FightTracker tracker = new(new FakeWorld());

    private static ActionHitEvent Hit(double seconds, uint source, uint target, long amount,
        HitKind kind = HitKind.Damage, bool crit = false, bool dh = false, uint owner = 0, uint action = 7406) =>
        new(T0.AddSeconds(seconds), source, owner, target, action, 1, kind, amount, crit, dh);

    [Fact]
    public void NoFightUntilAllyAndEnemyExchangeDamage()
    {
        tracker.Handle(Hit(0, Me, Healer, 100, HitKind.Heal));
        tracker.Handle(Hit(0, Boss, Add, 100));
        Assert.Null(tracker.Current);

        tracker.Handle(Hit(1, Me, Boss, 100));
        Assert.NotNull(tracker.Current);
        Assert.Equal(T0.AddSeconds(1), tracker.Current!.Start);
    }

    [Fact]
    public void EnemyHittingAllyStartsFight()
    {
        tracker.Handle(Hit(0, Boss, Me, 500));
        Assert.Equal(500, tracker.Current!.Combatants[Me].DamageTaken);
    }

    [Fact]
    public void DpsUsesWholeFightDuration()
    {
        tracker.Handle(Hit(0, Me, Boss, 1000));
        tracker.Handle(Hit(10, Me, Boss, 1000));
        tracker.Handle(Hit(10, Healer, Boss, 500));

        var summary = FightView.Summarize(tracker.Current!, T0.AddSeconds(10), mergePets: true);
        var me = summary.Rows.Single(r => r.Id == Me);
        Assert.Equal(200, me.Dps);
        Assert.Equal(0.8, me.DamageShare, 3);
        Assert.Equal(250, summary.RaidDps);
    }

    [Fact]
    public void CritDirectHitAndMaxHit()
    {
        tracker.Handle(Hit(0, Me, Boss, 100, crit: true, dh: true, action: 1));
        tracker.Handle(Hit(1, Me, Boss, 300, crit: true, action: 2));
        tracker.Handle(Hit(2, Me, Boss, 200));
        tracker.Handle(Hit(3, Me, Boss, 0, HitKind.Miss));

        var stats = tracker.Current!.Combatants[Me];
        Assert.Equal(3, stats.Hits);
        Assert.Equal(2, stats.Crits);
        Assert.Equal(1, stats.DirectHits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(300, stats.MaxHit);
        Assert.Equal(2u, stats.MaxHitActionId);
    }

    [Fact]
    public void DotTicksCountAsDamageButNotAsHits()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Me, 0, Boss, false, 50));

        var stats = tracker.Current!.Combatants[Me];
        Assert.Equal(150, stats.Damage);
        Assert.Equal(50, stats.DotDamage);
        Assert.Equal(1, stats.Hits);
    }

    [Fact]
    public void HealingAndHotsAreCredited()
    {
        tracker.Handle(Hit(0, Boss, Me, 1000));
        tracker.Handle(Hit(1, Healer, Me, 800, HitKind.Heal, crit: true));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(2), Healer, 0, Me, true, 200));

        var healer = tracker.Current!.Combatants[Healer];
        Assert.Equal(1000, healer.Healing);
        Assert.Equal(1, healer.HealCrits);
        Assert.Equal(1000, tracker.Current.Combatants[Me].HealingReceived);
    }

    [Fact]
    public void PetDamageMergesIntoOwnerWhenEnabled()
    {
        tracker.Handle(Hit(0, Me, Boss, 1000));
        tracker.Handle(Hit(1, MyPet, Boss, 500, owner: Me));

        var merged = FightView.Summarize(tracker.Current!, T0.AddSeconds(10), mergePets: true);
        Assert.Equal(1500, Assert.Single(merged.Rows).Damage);

        var split = FightView.Summarize(tracker.Current!, T0.AddSeconds(10), mergePets: false);
        Assert.Equal(2, split.Rows.Count);
        Assert.Contains(split.Rows, r => r.Name == "Eos" && r.Damage == 500);
    }

    [Fact]
    public void DeathsOfAlliesAreCounted()
    {
        tracker.Handle(Hit(0, Boss, Me, 1000));
        tracker.Handle(new DeathEvent(T0.AddSeconds(1), Me, Boss));
        tracker.Handle(new DeathEvent(T0.AddSeconds(1), Add, Me));
        Assert.Equal(1, tracker.Current!.Combatants[Me].Deaths);
        Assert.DoesNotContain(Add, tracker.Current.Combatants.Keys);
    }

    [Fact]
    public void FightIsNamedAfterMostDamagedEnemy()
    {
        tracker.Handle(Hit(0, Me, Add, 100));
        tracker.Handle(Hit(1, Me, Boss, 5000));
        Assert.Equal("Striking Dummy", tracker.Current!.Name);
    }

    [Fact]
    public void EndsWhenPartyLeavesCombatAfterGrace()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Update(T0.AddSeconds(0.5), partyInCombat: false); // flag not up yet: keep going
        Assert.NotNull(tracker.Current);

        tracker.Update(T0.AddSeconds(1), partyInCombat: true);
        tracker.Update(T0.AddSeconds(60), partyInCombat: true); // idle but still in combat (striking dummy)
        tracker.Update(T0.AddSeconds(61), partyInCombat: false); // combat drops here
        tracker.Update(T0.AddSeconds(70), partyInCombat: false); // within grace: timer held
        Assert.NotNull(tracker.Current);
        Assert.Equal(TimeSpan.FromSeconds(61), tracker.Current!.Duration(T0.AddSeconds(70)));

        Fight? ended = null;
        tracker.FightEnded += f => ended = f;
        tracker.Update(T0.AddSeconds(71), partyInCombat: false);
        Assert.Null(tracker.Current);
        Assert.NotNull(ended);
        Assert.Same(ended, tracker.Displayed);
        // Lasts until combat dropped: idle time in combat counts, the grace period doesn't.
        Assert.Equal(TimeSpan.FromSeconds(61), ended!.Duration(T0.AddHours(1)));
    }

    [Fact]
    public void LiveDurationMatchesFinalDuration()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Update(T0.AddSeconds(1), partyInCombat: true);
        var liveAtCombatEnd = tracker.Current!.Duration(T0.AddSeconds(20));

        tracker.Update(T0.AddSeconds(20), partyInCombat: false);
        Assert.Equal(liveAtCombatEnd, tracker.Current!.Duration(T0.AddSeconds(25))); // held, not counting
        tracker.Update(T0.AddSeconds(30), partyInCombat: false);
        Assert.Equal(liveAtCombatEnd, tracker.Last!.Duration(T0.AddHours(1)));
    }

    [Fact]
    public void ReenteringCombatWithinGraceKeepsFightGoing()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Update(T0.AddSeconds(1), partyInCombat: true);
        tracker.Update(T0.AddSeconds(5), partyInCombat: false);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.Current!.Duration(T0.AddSeconds(8))); // held
        tracker.Update(T0.AddSeconds(8), partyInCombat: true);
        Assert.Equal(TimeSpan.FromSeconds(9), tracker.Current!.Duration(T0.AddSeconds(9))); // resumed, gap counts
        tracker.Update(T0.AddSeconds(30), partyInCombat: false);
        tracker.Update(T0.AddSeconds(40), partyInCombat: false);
        Assert.Equal(TimeSpan.FromSeconds(30), tracker.Last!.Duration(T0.AddHours(1)));
    }

    [Fact]
    public void IdleTimeoutEndsFightAtLastHit()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(4, Me, Boss, 100));
        tracker.Update(T0.AddSeconds(33), partyInCombat: false);
        Assert.NotNull(tracker.Current);
        tracker.Update(T0.AddSeconds(34), partyInCombat: false);
        Assert.Null(tracker.Current);
        Assert.Equal(TimeSpan.FromSeconds(4), tracker.Last!.Duration(T0.AddHours(1)));
    }

    [Fact]
    public void ExplicitOutcomeIsRecorded()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.End(FightOutcome.Wipe, T0.AddSeconds(5));
        Assert.Equal(FightOutcome.Wipe, tracker.Last!.Outcome);
    }

    [Fact]
    public void NewFightStartsAfterPreviousEnded()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.End(FightOutcome.Clear, T0.AddSeconds(5));
        tracker.Handle(Hit(60, Me, Boss, 100));
        Assert.NotSame(tracker.Last, tracker.Current);
        Assert.Equal(T0.AddSeconds(60), tracker.Current!.Start);
    }
}
