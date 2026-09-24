using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
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

        Tracker = new FightTracker(this) { ZoneProvider = () => names.Zone(clientState.TerritoryType) };

        // Allies are filled on the first framework update: the constructor runs off the main thread,
        // where Dalamud forbids reading the object table.
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

    public ActorSnapshot? Lookup(uint entityId) =>
        source.Actors.Get(entityId) is { } actor ? new ActorSnapshot(actor.Name, actor.ClassJobId, actor.OwnerId) : null;

    public uint LocalPlayerId => objectTable.LocalPlayer?.EntityId ?? 0;

    private void OnEvent(CombatEvent e) => Tracker.Handle(e);

    private void OnFrameworkUpdate(IFramework _)
    {
        RefreshAllies();
        Tracker.Update(DateTime.UtcNow, IsPartyInCombat());
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
