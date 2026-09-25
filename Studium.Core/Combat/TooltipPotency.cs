using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Studium.Core.Combat;

/// <param name="Potency">The hit's potency ("with a potency of 160"), or null when the text names none.</param>
/// <param name="DotPotency">The damage-over-time potency (a plain "Potency: 25" line), or null.</param>
/// <param name="IsFixed">
/// The hit always lands at <paramref name="Potency"/>: no combo, positional, falloff or other potency that
/// depends on the situation. Only these hits are usable for measuring damage per potency.
/// </param>
/// <param name="HealPotency">A direct heal's potency ("Restores target's HP. Cure Potency: 500"), or null.</param>
/// <param name="HotPotency">The healing-over-time potency (Regen, Physis II, Medica II's regen...), or null.</param>
/// <param name="IsHealFixed">The heal always lands at <paramref name="HealPotency"/>, like <paramref name="IsFixed"/> for hits.</param>
public readonly record struct ActionPotency(
    int? Potency, int? DotPotency, bool IsFixed, int? HealPotency = null, int? HotPotency = null, bool IsHealFixed = false);

/// <summary>
/// Reads potencies from an action's tooltip, given in macro form (Lumina's <c>ToMacroString</c> of the English
/// <c>ActionTransient</c> description). Tooltips vary by job and level only through <c>if</c> macros on
/// <c>gnum68</c> (class / job ID) and <c>gnum72</c> (level), so they can be evaluated for any player.
/// </summary>
public static partial class TooltipPotency
{
    private const int JobParam = 68;
    private const int LevelParam = 72;

    public static ActionPotency Read(string macro, uint jobId, int level) => Parse(Evaluate(macro, jobId, level));

    /// <summary>The tooltip as the game shows it to a player of that job and level: plain text, lines split on "\n".</summary>
    public static string Evaluate(string macro, uint jobId, int level)
    {
        var output = new StringBuilder(macro.Length);
        var i = 0;
        Append(macro, ref i, output, jobId, level);
        return output.ToString();
    }

    /// <summary>Copies text to the output, resolving macros.</summary>
    private static void Append(string s, ref int i, StringBuilder output, uint jobId, int level)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                output.Append(s[i + 1]);
                i += 2;
            }
            else if (c == '<')
            {
                Macro(s, ref i, output, jobId, level);
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
    }

    /// <summary>One <c>&lt;name(args)&gt;</c> or <c>&lt;name&gt;</c>: <c>if</c> picks a branch, <c>br</c> breaks the line, the rest are dropped.</summary>
    private static void Macro(string s, ref int i, StringBuilder output, uint jobId, int level)
    {
        var nameStart = ++i;
        while (i < s.Length && char.IsLetterOrDigit(s[i]))
            i++;
        var name = s[nameStart..i];

        var args = new List<string>();
        if (i < s.Length && s[i] == '(')
        {
            i++;
            while (i < s.Length && s[i] != ')')
            {
                // The condition stays raw; branches are evaluated only once chosen.
                var start = i;
                SkipArgument(s, ref i);
                args.Add(s[start..i]);
                if (i < s.Length && s[i] == ',')
                    i++;
            }
            i++; // ')'
        }
        if (i < s.Length && s[i] == '>')
            i++;

        if (name == "br")
            output.Append('\n');
        else if (name == "if" && args.Count > 0)
        {
            var branch = Condition(args[0], jobId, level) ? 1 : 2;
            if (branch < args.Count)
            {
                var j = 0;
                Append(args[branch], ref j, output, jobId, level);
            }
        }
    }

    /// <summary>Moves past one macro argument, skipping nested macros, brackets and escapes.</summary>
    private static void SkipArgument(string s, ref int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '[')
            {
                // A condition: its comparison may contain '>' or '<', so skip it whole.
                var end = s.IndexOf(']', i);
                i = end < 0 ? s.Length : end + 1;
                continue;
            }
            if (c is '<' or '(')
                depth++;
            else if (c == '>' || (c == ')' && depth > 0))
                depth--;
            else if (depth == 0 && c is ',' or ')')
                return;
            i++;
        }
    }

    /// <summary><c>[gnum68==23]</c>, <c>[gnum72&gt;=94]</c>... Other parameters (gatherer level, pet IDs) read as 0.</summary>
    private static bool Condition(string condition, uint jobId, int level)
    {
        var match = ConditionRegex().Match(condition);
        if (!match.Success)
            return false;
        var param = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        long left = param switch { JobParam => jobId, LevelParam => level, _ => 0 };
        var right = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "==" => left == right,
            "!=" => left != right,
            ">=" => left >= right,
            "<=" => left <= right,
            ">" => left > right,
            "<" => left < right,
            _ => false,
        };
    }

    public static ActionPotency Parse(string text)
    {
        var hit = HitPotencyRegex().Match(text);
        int? potency = hit.Success ? Number(hit.Groups[1].Value) : null;

        int? dotPotency = null;
        var otherPotencies = 0;
        foreach (Match line in LabelledPotencyRegex().Matches(text))
        {
            if (line.Groups[1].Value.Length == 0 && dotPotency == null)
                dotPotency = Number(line.Groups[2].Value);
            else if (line.Groups[1].Value != "Cure")
                otherPotencies++; // Combo Potency, Rear Combo Potency, Fury Potency...
        }

        var variable = VariableRegex().IsMatch(text);
        var isFixed = potency != null
                      && otherPotencies == 0
                      && HitPotencyRegex().Matches(text).Count == 1
                      && !variable;

        var (healPotency, hotPotency) = Heals(text);
        var isHealFixed = healPotency != null && otherPotencies == 0 && !variable;
        return new ActionPotency(potency, dotPotency, isFixed, healPotency, hotPotency, isHealFixed);
    }

    /// <summary>
    /// "Cure Potency: N" lines. After a "…Effect: Regen" line, the next one is the regen's (Medica II, Aspected
    /// Benefic, Kerachole). Healing-over-time actions (Regen, Physis II, Asylum) have only the one; direct heals
    /// ("Restores target's HP.") open with theirs.
    /// </summary>
    private static (int? Heal, int? Hot) Heals(string text)
    {
        var cures = CurePotencyRegex().Matches(text);
        if (cures.Count == 0)
            return (null, null);

        int? heal = null, hot = null;
        var regen = RegenLineRegex().Match(text);
        if (regen.Success)
        {
            var after = cures.FirstOrDefault(c => c.Index > regen.Index);
            hot = after != null ? Number(after.Groups[1].Value) : null;
        }
        else if (HealOverTimeRegex().IsMatch(text))
        {
            hot = Number(cures[0].Groups[1].Value);
        }

        if (text.StartsWith("Restores", StringComparison.Ordinal) && (!regen.Success || cures[0].Index < regen.Index))
            heal = Number(cures[0].Groups[1].Value);
        return (heal, hot);
    }

    private static int Number(string digits) => int.Parse(digits.Replace(",", ""), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\[gnum(\d+)\s*(==|!=|>=|<=|>|<)\s*(\d+)\]")]
    private static partial Regex ConditionRegex();

    [GeneratedRegex(@"potency of ([\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HitPotencyRegex();

    /// <summary>"Potency: 25" (the DoT line) or "Combo Potency: 300": a label ending in "Potency", then the number.</summary>
    [GeneratedRegex(@"^([A-Za-z' ]*?) ?Potency: *([\d,]+)", RegexOptions.Multiline)]
    private static partial Regex LabelledPotencyRegex();

    [GeneratedRegex(@"^Cure Potency: *([\d,]+)", RegexOptions.Multiline)]
    private static partial Regex CurePotencyRegex();

    [GeneratedRegex(@"Effect: Regen *$", RegexOptions.Multiline)]
    private static partial Regex RegenLineRegex();

    [GeneratedRegex(@"healing over time|Gradually restores", RegexOptions.IgnoreCase)]
    private static partial Regex HealOverTimeRegex();

    /// <summary>Wording that makes a hit's potency depend on the situation.</summary>
    [GeneratedRegex(@"executed from|first enemy|remaining enemies|potency (is )?increase|increases potency|increased potency|increasing potency|when used|if used|stack", RegexOptions.IgnoreCase)]
    private static partial Regex VariableRegex();
}
