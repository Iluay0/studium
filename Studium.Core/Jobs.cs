namespace Studium.Core;

/// <summary>Job abbreviations and colours (FFLogs palette), keyed by ClassJob row ID.</summary>
public static class Jobs
{
    private static readonly Dictionary<uint, (string Abbreviation, uint Rgb)> Table = new()
    {
        // Tanks
        [1] = ("GLA", 0xA8D2E6), [19] = ("PLD", 0xA8D2E6),
        [3] = ("MRD", 0xCF2621), [21] = ("WAR", 0xCF2621),
        [32] = ("DRK", 0xD126CC),
        [37] = ("GNB", 0x796D30),
        // Healers
        [6] = ("CNJ", 0xFFF0DC), [24] = ("WHM", 0xFFF0DC),
        [28] = ("SCH", 0x8657FF),
        [33] = ("AST", 0xFFE74A),
        [40] = ("SGE", 0x80A0F0),
        // Melee
        [2] = ("PGL", 0xD69C00), [20] = ("MNK", 0xD69C00),
        [4] = ("LNC", 0x4164CD), [22] = ("DRG", 0x4164CD),
        [29] = ("ROG", 0xAF1964), [30] = ("NIN", 0xAF1964),
        [34] = ("SAM", 0xE46D04),
        [39] = ("RPR", 0x965A90),
        [41] = ("VPR", 0x108210),
        // Ranged
        [5] = ("ARC", 0x91BA5E), [23] = ("BRD", 0x91BA5E),
        [31] = ("MCH", 0x6EE1D6),
        [38] = ("DNC", 0xE2B0AF),
        // Casters
        [7] = ("THM", 0xA579D6), [25] = ("BLM", 0xA579D6),
        [26] = ("ACN", 0x2D9B78), [27] = ("SMN", 0x2D9B78),
        [35] = ("RDM", 0xE87B7B),
        [42] = ("PCT", 0xFC92E1),
        [36] = ("BLU", 0x2B5CD6),
    };

    public const uint FallbackRgb = 0x8A8A8A;

    public static string Abbreviation(uint jobId) => Table.TryGetValue(jobId, out var job) ? job.Abbreviation : string.Empty;

    /// <summary>Colour as 0xRRGGBB; grey for pets, NPCs and unknown jobs.</summary>
    public static uint Rgb(uint jobId) => Table.TryGetValue(jobId, out var job) ? job.Rgb : FallbackRgb;

    /// <summary>The game's job icon (framed style).</summary>
    public static uint IconId(uint jobId) => Table.ContainsKey(jobId) ? 62100 + jobId : 0;
}
