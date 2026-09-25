using Studium.Core.Combat;

namespace Studium.Core.Fights;

/// <summary>
/// A DoT tick on an enemy (or HoT tick on a player) as the game sent it: one amount for every DoT (HoT) on the
/// target. <see cref="Counted"/> are the sources whose share is recorded, decided when the tick landed.
/// </summary>
public sealed record DotTick(DateTime Time, uint TargetId, long Amount, IReadOnlyList<DotOnTarget> Dots, IReadOnlySet<uint> Counted, long Overheal = 0);

/// <summary>One DoT's (HoT's) share of one tick, with its part of the tick's overheal.</summary>
public readonly record struct DotShare(uint SourceId, uint SourceOwnerId, uint StatusId, uint ActionId, long Amount, long Overheal = 0);

/// <summary>
/// Splits combined DoT (HoT) ticks between the DoTs (HoTs) that were on the target. The game only sends the sum,
/// so this is an estimate: each is weighted by its potency × its owner's damage (healing) per potency, the median
/// of their fixed-potency, non-crit, non-DH hits (heals). A pet's HoT uses its owner's rate. Owners with no such
/// hit yet count as the median player; unknown potencies count as the tick's average one.
/// </summary>
public static class DotSplit
{
    public static IEnumerable<DotShare> Split(DotTick tick, IReadOnlyDictionary<uint, double> damagePerPotency)
    {
        var dots = tick.Dots;
        if (dots.Count == 0)
            yield break;
        if (dots.Count == 1)
        {
            yield return new DotShare(dots[0].SourceId, dots[0].SourceOwnerId, dots[0].StatusId, dots[0].ActionId, tick.Amount, tick.Overheal);
            yield break;
        }

        var known = dots.Where(d => d.Potency > 0).ToList();
        double fallbackPotency = known.Count > 0 ? known.Average(d => d.Potency) : 1;
        var fallbackRate = damagePerPotency.Count > 0 ? Median(damagePerPotency.Values.ToList()) : 1;

        var weights = new double[dots.Count];
        for (var i = 0; i < dots.Count; i++)
        {
            var potency = dots[i].Potency > 0 ? dots[i].Potency : fallbackPotency;
            var rateOwner = dots[i].SourceOwnerId != 0 ? dots[i].SourceOwnerId : dots[i].SourceId;
            var rate = damagePerPotency.TryGetValue(rateOwner, out var r) ? r : fallbackRate;
            weights[i] = potency * rate;
        }

        var total = weights.Sum();
        long given = 0, givenOverheal = 0;
        for (var i = 0; i < dots.Count; i++)
        {
            // The last share takes the rounding remainder, so shares always add up to the tick.
            var last = i == dots.Count - 1;
            var amount = last ? tick.Amount - given : (long)Math.Round(tick.Amount * weights[i] / total);
            var overheal = last ? tick.Overheal - givenOverheal : (long)Math.Round(tick.Overheal * weights[i] / total);
            given += amount;
            givenOverheal += overheal;
            yield return new DotShare(dots[i].SourceId, dots[i].SourceOwnerId, dots[i].StatusId, dots[i].ActionId, amount, overheal);
        }
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
