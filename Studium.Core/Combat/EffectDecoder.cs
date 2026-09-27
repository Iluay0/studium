namespace Studium.Core.Combat;

public readonly record struct DecodedEffect(HitKind Kind, long Amount, bool Crit, bool DirectHit, bool TargetsSource);

public static class EffectDecoder
{
    private const byte TypeMiss = 1;
    private const byte TypeDamage = 3;
    private const byte TypeHeal = 4;
    private const byte TypeBlockedDamage = 5;
    private const byte TypeParriedDamage = 6;
    private const byte TypeInvulnerable = 7;

    private const byte DamageCritFlag = 0x20;
    private const byte DamageDirectHitFlag = 0x40;
    private const byte HealCritFlag = 0x20;
    private const byte ExtendedValueFlag = 0x40;
    private const byte SourceEntryFlag = 0x80;

    public const uint ActorControlDeath = 0x06;
    public const uint ActorControlHot = 0x604;
    public const uint ActorControlDot = 0x605;

    public static bool TryDecode(byte type, byte param0, byte param1, byte param3, byte param4, ushort value, out DecodedEffect effect)
    {
        var amount = (long)value;
        if ((param4 & ExtendedValueFlag) != 0)
            amount += (long)param3 << 16;
        var targetsSource = (param4 & SourceEntryFlag) != 0;

        effect = type switch
        {
            TypeDamage => Damage(HitKind.Damage),
            TypeBlockedDamage => Damage(HitKind.BlockedDamage),
            TypeParriedDamage => Damage(HitKind.ParriedDamage),
            TypeHeal => new DecodedEffect(HitKind.Heal, amount, (param1 & HealCritFlag) != 0, false, targetsSource),
            TypeMiss => new DecodedEffect(HitKind.Miss, 0, false, false, targetsSource),
            TypeInvulnerable => new DecodedEffect(HitKind.Invulnerable, 0, false, false, targetsSource),
            _ => default,
        };
        return type is TypeDamage or TypeBlockedDamage or TypeParriedDamage or TypeHeal or TypeMiss or TypeInvulnerable;

        DecodedEffect Damage(HitKind kind) =>
            new(kind, amount, (param0 & DamageCritFlag) != 0, (param0 & DamageDirectHitFlag) != 0, targetsSource);
    }

    // ActorControl 0x604 (HoT) and 0x605 (DoT): arg1 status ID (usually 0), arg2 amount, arg3 source.
    public static bool TryDecodeTick(uint category, uint arg2, uint arg3, out bool isHeal, out long amount, out uint sourceId)
    {
        isHeal = category == ActorControlHot;
        amount = arg2;
        sourceId = arg3;
        return category is ActorControlHot or ActorControlDot
               && amount > 0 && sourceId != 0 && sourceId != InvalidEntityId;
    }

    public const uint InvalidEntityId = 0xE0000000;
}
