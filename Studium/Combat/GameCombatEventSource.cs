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

    private readonly GameNames names;

    public GameCombatEventSource(IGameInteropProvider interop, IObjectTable objectTable, IPluginLog log, GameNames names)
    {
        this.objectTable = objectTable;
        this.log = log;
        this.names = names;
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
                var overheal = decoded.Kind == HitKind.Heal ? OverhealOn(effectTarget, decoded.Amount) : 0;
                EventReceived?.Invoke(new ActionHitEvent(
                    now, casterId, ownerId, effectTarget, header->ActionId, header->ActionType,
                    decoded.Kind, decoded.Amount, decoded.Crit, decoded.DirectHit, overheal));
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
                        var overheal = isHeal ? OverhealOn(entityId, amount) : 0;
                        var statuses = TickCandidates(entityId, sourceId, isHeal, arg1);
                        EventReceived?.Invoke(new PeriodicTickEvent(now, sourceId, Actors.OwnerOf(sourceId), entityId, isHeal, amount, overheal, statuses));
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
    private IReadOnlyList<uint> TickCandidates(uint targetId, uint sourceId, bool isHeal, uint statusArg)
    {
        if (statusArg != 0)
            return [statusArg];
        if (objectTable.SearchByEntityId(targetId) is not IBattleChara target)
            return [];

        var candidates = new List<uint>(2);
        foreach (var status in target.StatusList)
        {
            if (status.StatusId == 0 || status.SourceId != sourceId)
                continue;
            var info = names.Status(status.StatusId);
            if (isHeal ? info.IsHot : info.IsDot)
                candidates.Add(status.StatusId);
        }
        return candidates;
    }

    private long OverhealOn(uint targetId, long amount) =>
        objectTable.SearchByEntityId(targetId) is ICharacter target
            ? Overheal.Estimate(amount, target.CurrentHp, target.MaxHp)
            : 0;

    private static uint NormalizeId(uint id) => id == EffectDecoder.InvalidEntityId ? 0 : id;
}
