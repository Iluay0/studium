using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Studium.Core;
using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Combat;

/// <summary>
/// Feeds game events into the <see cref="FightTracker"/> and tells it who is in the party and in combat.
/// Everything runs on the game's main thread (hooks, framework update and drawing).
/// </summary>
public sealed class FightService : ICombatWorld, IDisposable
{
    private readonly GameCombatEventSource source;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IDutyState dutyState;
    private readonly HashSet<uint> allies = new();

    public FightTracker Tracker { get; }

    public FightService(
        GameCombatEventSource source, IFramework framework, ICondition condition, IObjectTable objectTable,
        IPartyList partyList, IDutyState dutyState, IClientState clientState, GameNames names)
    {
        this.source = source;
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.dutyState = dutyState;

        Tracker = new FightTracker(this)
        {
            ContextProvider = () =>
            {
                var me = objectTable.LocalPlayer;
                return new FightContext(
                    names.Zone(clientState.TerritoryType),
                    me?.Name.TextValue ?? string.Empty,
                    me?.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    me?.EntityId ?? 0);
            },
        };

        // Allies are filled on the first framework update: the constructor runs off the main thread,
        // where Dalamud forbids reading the object table.
        source.TracksHp = IsAlly;
        source.EventReceived += OnEvent;
        framework.Update += OnFrameworkUpdate;
        dutyState.DutyWiped += OnDutyWiped;
        dutyState.DutyCompleted += OnDutyCompleted;
    }

    public void Dispose()
    {
        source.EventReceived -= OnEvent;
        framework.Update -= OnFrameworkUpdate;
        dutyState.DutyWiped -= OnDutyWiped;
        dutyState.DutyCompleted -= OnDutyCompleted;
    }

    public bool IsAlly(uint entityId) => allies.Contains(entityId);

    /// <summary>Called once per combatant per fight, so it re-reads the actor: jobs can change between fights.</summary>
    public ActorSnapshot? Lookup(uint entityId)
    {
        source.Actors.Refresh(entityId);
        return source.Actors.Get(entityId) is { } actor ? new ActorSnapshot(actor.Name, actor.ClassJobId, actor.OwnerId) : null;
    }

    public bool IsDead(uint entityId) => objectTable.SearchByEntityId(entityId) is { IsDead: true };

    public bool IsEngaged(uint entityId) =>
        objectTable.SearchByEntityId(entityId) is IBattleChara { IsDead: false } enemy
        && (enemy.StatusFlags.HasFlag(StatusFlags.InCombat)
            || enemy.TargetObjectId is not (0 or EffectDecoder.InvalidEntityId));

    public uint LocalPlayerId => objectTable.LocalPlayer?.EntityId ?? 0;

    /// <summary>The logged-in character's FFLogs page, or null when logged out or the region is unknown.</summary>
    public string? CurrentFflogsUrl
    {
        get
        {
            if (objectTable.LocalPlayer is not { } me || me.HomeWorld.ValueNullable is not { } world)
                return null;
            var region = world.DataCenter.ValueNullable?.Region.RowId ?? 0;
            return FflogsLinks.CharacterUrl(region, world.Name.ExtractText(), me.Name.TextValue);
        }
    }

    /// <summary>"Name @ World" of the logged-in character (matches HistoryFilter.CharacterKey), or null.</summary>
    public string? CurrentCharacterKey =>
        objectTable.LocalPlayer is { } me
            ? $"{me.Name.TextValue} @ {me.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty}"
            : null;

    private void OnEvent(CombatEvent e) => Tracker.Handle(e);

    /// <summary>You or a party member is in combat (as of the last frame).</summary>
    public bool PartyInCombat { get; private set; }

    /// <summary>Inside a duty instance (as of the last frame).</summary>
    public bool InDuty { get; private set; }

    private void OnFrameworkUpdate(IFramework _)
    {
        RefreshAllies();
        PartyInCombat = IsPartyInCombat();
        InDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
        Tracker.Update(DateTime.UtcNow, PartyInCombat);
    }

    private void RefreshAllies()
    {
        allies.Clear();
        if (objectTable.LocalPlayer is { } me)
            allies.Add(me.EntityId);
        foreach (var member in partyList)
        {
            if (member.EntityId != 0)
                allies.Add(member.EntityId);
        }
    }

    private bool IsPartyInCombat()
    {
        if (condition[ConditionFlag.InCombat])
            return true;
        foreach (var member in partyList)
        {
            if (member.GameObject is ICharacter character && character.StatusFlags.HasFlag(StatusFlags.InCombat))
                return true;
        }
        return false;
    }

    private void OnDutyWiped(IDutyStateEventArgs _) => Tracker.End(FightOutcome.Wipe, DateTime.UtcNow);

    private void OnDutyCompleted(IDutyStateEventArgs _) => Tracker.End(FightOutcome.Clear, DateTime.UtcNow);
}
