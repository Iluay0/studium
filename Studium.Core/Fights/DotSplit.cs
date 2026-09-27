using Studium.Core.Combat;

namespace Studium.Core.Fights;

public sealed record DotTick(DateTime Time, uint TargetId, long Amount, IReadOnlyList<DotOnTarget> Dots, IReadOnlySet<uint> Counted,
    long Overheal = 0, IReadOnlyList<double?>? AppliedRates = null);

public readonly record struct DotShare(uint SourceId, uint SourceOwnerId, uint StatusId, uint ActionId, long Amount, long Overheal = 0);

// The game only sends the sum, so this is an estimate. Each DoT is weighted by potency times a
// damage-per-potency rate: the hit that applied it, or if that crit, the median of the player's clean
// DoT-applying hits, or the median of all their clean fixed-potency hits. Pets use their owner's rate. Players
// with no hits yet use the median player; unknown potencies use the tick's average.
public static class DotSplit
{
    public static IEnumerable<DotShare> Split(DotTick tick, IReadOnlyDictionary<uint, double> damagePerPotency,
        IReadOnlyDictionary<uint, double>? dotRates = null)
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
            var rate = tick.AppliedRates?[i] is { } applied ? applied
                : dotRates != null && dotRates.TryGetValue(dots[i].SourceId, out var d) ? d
                : damagePerPotency.TryGetValue(rateOwner, out var r) ? r
                : fallbackRate;
            weights[i] = potency * rate;
        }

        var total = weights.Sum();
        long given = 0, givenOverheal = 0;
        for (var i = 0; i < dots.Count; i++)
        {
            // The last share takes the rounding remainder so the shares add up.
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
