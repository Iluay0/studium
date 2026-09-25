using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Studium.Core.Combat;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace Studium.Combat;

/// <summary>Raw ActorControl packet, surfaced for the debug window so unknown categories can be identified.</summary>
public readonly record struct ActorControlRecord(DateTime Time, uint EntityId, uint Category, uint Arg1, uint Arg2, uint Arg3, uint Arg4);

/// <summary>
/// Turns game function hooks (all resolved by FFXIVClientStructs, no hand-written signatures)
/// into <see cref="CombatEvent"/>s. Keep this layer thin: decoding lives in <see cref="EffectDecoder"/>.
/// </summary>
public sealed unsafe class GameCombatEventSource : ICombatEventSource, IDisposable
{
    private delegate void ReceiveActionEffectDelegate(
        uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds);

    private delegate void ActorControlDelegate(
        uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded);

    private delegate void ActorCastDelegate(uint entityId, ActorCastPacket* packet);

    private const int EffectsPerTarget = 8;
    /// <summary>ActionEffect header ActionType for regular actions (not items, mounts...).</summary>
    private const byte ActionTypeAction = 1;

    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly Hook<ReceiveActionEffectDelegate>? actionEffectHook;
    private readonly Hook<ActorControlDelegate>? actorControlHook;
    private readonly Hook<ActorCastDelegate>? actorCastHook;

    public event Action<CombatEvent>? EventReceived;
    public event Action<ActorControlRecord>? ActorControlReceived;

    /// <summary>Hook name → error message, for hooks that failed to install.</summary>
    public IReadOnlyDictionary<string, string> HookErrors => hookErrors;
    private readonly Dictionary<string, string> hookErrors = new();

    public ActorCache Actors { get; }

    /// <summary>Whose HP to read with each event (party members, for death recaps). Reading everyone's would be wasted work.</summary>
    public Func<uint, bool> TracksHp { get; set; } = _ => false;

    private readonly GameNames names;
    private readonly PotencyTable potencies;

    public GameCombatEventSource(IGameInteropProvider interop, IObjectTable objectTable, IPluginLog log, GameNames names, PotencyTable potencies)
    {
        this.objectTable = objectTable;
        this.log = log;
        this.names = names;
        this.potencies = potencies;
        Actors = new ActorCache(objectTable);

        actionEffectHook = TryHook<ReceiveActionEffectDelegate>(interop, "ActionEffect",
            () => ActionEffectHandler.Addresses.Receive.Value, OnReceiveActionEffect);
        actorControlHook = TryHook<ActorControlDelegate>(interop, "ActorControl",
            () => PacketDispatcher.Addresses.HandleActorControlPacket.Value, OnActorControl);
        actorCastHook = TryHook<ActorCastDelegate>(interop, "ActorCast",
            () => PacketDispatcher.Addresses.HandleActorCastPacket.Value, OnActorCast);
    }

    public void Dispose()
    {
        actionEffectHook?.Dispose();
        actorControlHook?.Dispose();
        actorCastHook?.Dispose();
    }

    private Hook<T>? TryHook<T>(IGameInteropProvider interop, string name, Func<nint> address, T detour) where T : Delegate
    {
        try
        {
            var hook = interop.HookFromAddress(address(), detour);
            hook.Enable();
            return hook;
        }
        catch (Exception ex)
        {
            hookErrors[name] = ex.Message;
            log.Error(ex, $"Failed to hook {name}");
            return null;
        }
    }

    private void OnReceiveActionEffect(
        uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            if (header->NumTargets > 0)
                EmitActionEffect(casterEntityId, casterPtr, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            log.Error(ex, "ActionEffect handler failed");
        }

        actionEffectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
    }

    private void EmitActionEffect(
        uint casterId, Character* casterPtr,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetIds)
    {
        var now = DateTime.UtcNow;
        var ownerId = casterPtr != null ? NormalizeId(casterPtr->OwnerId) : Actors.OwnerOf(casterId);
        Actors.Observe(casterId);
        // Players' fixed potencies measure their damage (healing) per potency, which weighs their DoTs (HoTs) in combined ticks.
        var isPlayerAction = casterPtr != null && casterPtr->GameObject.ObjectKind == ObjectKind.Pc && header->ActionType == ActionTypeAction;
        int? Potency(bool heal) => isPlayerAction
            ? potencies.FixedPotency(header->ActionId, casterPtr->CharacterData.ClassJob, casterPtr->CharacterData.Level, heal)
            : null;

        var entries = (ActionEffectHandler.Effect*)effects;
        for (var t = 0; t < header->NumTargets; t++)
        {
            var targetId = targetIds[t].ObjectId;
            if (targetId == 0)
                continue;
            Actors.Observe(targetId);

            for (var e = 0; e < EffectsPerTarget; e++)
            {
                var entry = entries[(t * EffectsPerTarget) + e];
                if (!EffectDecoder.TryDecode(entry.Type, entry.Param0, entry.Param1, entry.Param3, entry.Param4, entry.Value, out var decoded))
                    continue;

                // "Source entries" land on the caster (e.g. a self-heal riding on a damage action).
                var effectTarget = decoded.TargetsSource ? casterId : targetId;
                var isHeal = decoded.Kind == HitKind.Heal;
                var hp = isHeal || TracksHp(effectTarget) ? ReadHp(effectTarget) : null;
                var overheal = isHeal && hp is { } h ? Overheal.Estimate(decoded.Amount, h.Current, h.Max) : 0;
                var tracked = TracksHp(effectTarget);
                EventReceived?.Invoke(new ActionHitEvent(
                    now, casterId, ownerId, effectTarget, header->ActionId, header->ActionType,
                    decoded.Kind, decoded.Amount, decoded.Crit, decoded.DirectHit, overheal,
                    tracked ? hp : null, tracked ? ReadDefense(effectTarget, casterId) : null,
                    decoded.Kind switch { HitKind.Damage => Potency(false), HitKind.Heal => Potency(true), _ => null }));
            }
        }
    }

    private void OnActorControl(
        uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        try
        {
            var now = DateTime.UtcNow;
            ActorControlReceived?.Invoke(new ActorControlRecord(now, entityId, category, arg1, arg2, arg3, arg4));

            switch (category)
            {
                case EffectDecoder.ActorControlHot:
                case EffectDecoder.ActorControlDot:
                    if (EffectDecoder.TryDecodeTick(category, arg2, arg3, out var isHeal, out var amount, out var sourceId))
                    {
                        Actors.Observe(sourceId);
                        Actors.Observe(entityId);
                        var hp = isHeal || TracksHp(entityId) ? ReadHp(entityId) : null;
                        var overheal = isHeal && hp is { } h ? Overheal.Estimate(amount, h.Current, h.Max) : 0;
                        var statuses = TickCandidates(entityId, sourceId, isHeal, arg1, anySource: !isHeal && TracksHp(entityId));
                        var tracked = TracksHp(entityId);
                        // DoTs on party members come from enemies, whose potencies aren't known: those stay unsplit.
                        var dots = !isHeal && tracked ? null : OverTimeOn(entityId, sourceId, arg1, isHeal);
                        EventReceived?.Invoke(new PeriodicTickEvent(now, sourceId, Actors.OwnerOf(sourceId), entityId, isHeal, amount, overheal,
                            statuses, tracked ? hp : null, tracked ? ReadDefense(entityId, sourceId) : null, dots));
                    }
                    break;
                case EffectDecoder.ActorControlDeath:
                    Actors.Observe(entityId);
                    EventReceived?.Invoke(new DeathEvent(now, entityId, arg1));
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "ActorControl handler failed");
        }

        actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
    }

    private void OnActorCast(uint entityId, ActorCastPacket* packet)
    {
        try
        {
            Actors.Observe(entityId);
            EventReceived?.Invoke(new CastStartEvent(DateTime.UtcNow, entityId, packet->TargetEntityId, packet->ActionId, packet->CastTime));
        }
        catch (Exception ex)
        {
            log.Error(ex, "ActorCast handler failed");
        }

        actorCastHook!.Original(entityId, packet);
    }

    /// <summary>
    /// Which of the source's DoTs (or HoTs) could this tick be? Ground effects name their status in arg1;
    /// otherwise it's the source's matching statuses on the target right now.
    /// </summary>
    /// <param name="anySource">
    /// Every DoT on the target, whoever applied it: for DoTs on party members, where the enemies' ticks are summed
    /// too and can't be split, so the row names them all.
    /// </param>
    private IReadOnlyList<uint> TickCandidates(uint targetId, uint sourceId, bool isHeal, uint statusArg, bool anySource)
    {
        if (statusArg != 0)
            return [statusArg];
        if (objectTable.SearchByEntityId(targetId) is not IBattleChara target)
            return [];

        var candidates = new List<uint>(2);
        foreach (var status in target.StatusList)
        {
            if (status.StatusId == 0 || (!anySource && status.SourceId != sourceId))
                continue;
            var info = names.Status(status.StatusId);
            if (isHeal ? info.IsHot : info.IsDot)
                candidates.Add(status.StatusId);
        }
        return candidates;
    }

    /// <summary>
    /// Every DoT on an enemy (or HoT on a player) when a tick lands, from every player: the game sums them into one
    /// tick. A ground effect names its status in arg1 and ticks alone, so it's the only one. Null when none can be read.
    /// </summary>
    private IReadOnlyList<DotOnTarget>? OverTimeOn(uint targetId, uint sourceId, uint statusArg, bool heal)
    {
        if (statusArg != 0)
            return [new DotOnTarget(statusArg, sourceId, 0, 0, Actors.OwnerOf(sourceId))];
        if (objectTable.SearchByEntityId(targetId) is not IBattleChara target)
            return null;

        var found = new List<DotOnTarget>(4);
        foreach (var status in target.StatusList)
        {
            if (status.StatusId == 0)
                continue;
            var info = names.Status(status.StatusId);
            if (!(heal ? info.IsHot : info.IsDot))
                continue;
            // A faerie's HoT (Whispering Dawn) is weighed with its owner's job, level and healing per potency.
            var ownerId = Actors.OwnerOf(status.SourceId);
            var (job, level) = JobAndLevel(ownerId != 0 ? ownerId : status.SourceId);
            var (actionId, potency) = job == 0 ? (0u, 0) : potencies.OverTime(status.StatusId, job, level, heal);
            found.Add(new DotOnTarget(status.StatusId, status.SourceId, actionId, potency, ownerId));
        }
        return found.Count > 0 ? found : null;
    }

    private (uint Job, int Level) JobAndLevel(uint entityId) =>
        objectTable.SearchByEntityId(entityId) is IBattleChara chara ? (chara.ClassJob.RowId, chara.Level) : (Actors.Get(entityId)?.ClassJobId ?? 0, 100);

    /// <summary>Statuses with more time left than this are background (e.g. long buffs), not part of a death.</summary>
    private const float LongStatusSeconds = 300;

    /// <summary>
    /// A party member's defences as a hit lands: their statuses minus noise, the party's debuffs on the attacker,
    /// and their shield. Only called for party members, so the extra object lookups stay cheap.
    /// </summary>
    public DefenseSnapshot? ReadDefense(uint targetId, uint attackerId)
    {
        if (objectTable.SearchByEntityId(targetId) is not IBattleChara target)
            return null;

        var onTarget = new List<StatusSnapshot>();
        foreach (var status in target.StatusList)
        {
            if (status.StatusId == 0 || status.RemainingTime > LongStatusSeconds || names.Status(status.StatusId).IsNoise)
                continue;
            onTarget.Add(Snapshot(status.StatusId, status.Param, status.SourceId));
        }

        var onAttacker = new List<StatusSnapshot>();
        if (attackerId != 0 && attackerId != targetId && objectTable.SearchByEntityId(attackerId) is IBattleChara attacker)
        {
            foreach (var status in attacker.StatusList)
            {
                if (status.StatusId != 0 && TracksHp(status.SourceId) && names.Status(status.StatusId).IsPartyDebuff)
                    onAttacker.Add(Snapshot(status.StatusId, status.Param, status.SourceId));
            }
        }

        return new DefenseSnapshot(onTarget, onAttacker, target.ShieldPercentage);
    }

    private StatusSnapshot Snapshot(uint statusId, ushort param, uint sourceId)
    {
        Actors.Observe(sourceId);
        var stacks = names.Status(statusId).MaxStacks > 1 ? (byte)Math.Min(param, (ushort)255) : (byte)0;
        var source = sourceId is 0 or EffectDecoder.InvalidEntityId ? string.Empty : Actors.NameOf(sourceId);
        return new StatusSnapshot(statusId, stacks, source);
    }

    /// <summary>The target's HP right now, i.e. before this event is applied.</summary>
    private TargetHp? ReadHp(uint targetId) =>
        objectTable.SearchByEntityId(targetId) is ICharacter target ? new TargetHp(target.CurrentHp, target.MaxHp) : null;

    private static uint NormalizeId(uint id) => id == EffectDecoder.InvalidEntityId ? 0 : id;
}
