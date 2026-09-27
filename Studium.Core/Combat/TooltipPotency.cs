using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Studium.Core.Combat;

public readonly record struct ActionPotency(
    int? Potency, int? DotPotency, bool IsFixed, int? HealPotency = null, int? HotPotency = null, bool IsHealFixed = false);

// Tooltips only vary through if macros on gnum68 (job) and gnum72 (level).
public static partial class TooltipPotency
{
    private const int JobParam = 68;
    private const int LevelParam = 72;

    public static ActionPotency Read(string macro, uint jobId, int level) => Parse(Evaluate(macro, jobId, level));

    public static string Evaluate(string macro, uint jobId, int level)
    {
        var output = new StringBuilder(macro.Length);
        var i = 0;
        Append(macro, ref i, output, jobId, level);
        return output.ToString();
    }

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
                var start = i;
                SkipArgument(s, ref i);
                args.Add(s[start..i]);
                if (i < s.Length && s[i] == ',')
                    i++;
            }
            i++;
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
                // A condition can contain > or <, so skip it whole.
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
                otherPotencies++;
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

    // After an 'Effect: Regen' line the next Cure Potency is the regen's. HoT actions have only that one; direct
    // heals start with theirs.
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

    [GeneratedRegex(@"^([A-Za-z' ]*?) ?Potency: *([\d,]+)", RegexOptions.Multiline)]
    private static partial Regex LabelledPotencyRegex();

    [GeneratedRegex(@"^Cure Potency: *([\d,]+)", RegexOptions.Multiline)]
    private static partial Regex CurePotencyRegex();

    [GeneratedRegex(@"Effect: Regen *$", RegexOptions.Multiline)]
    private static partial Regex RegenLineRegex();

    [GeneratedRegex(@"healing over time|Gradually restores", RegexOptions.IgnoreCase)]
    private static partial Regex HealOverTimeRegex();

    [GeneratedRegex(@"executed from|first enemy|remaining enemies|potency (is )?increase|increases potency|increased potency|increasing potency|when used|if used|stack", RegexOptions.IgnoreCase)]
    private static partial Regex VariableRegex();
}
