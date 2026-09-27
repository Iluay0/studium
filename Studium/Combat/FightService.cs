using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Studium.Core;
using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Combat;

public sealed class FightService : ICombatWorld, IDisposable
{
    private readonly GameCombatEventSource source;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IDutyState dutyState;
    private readonly HashSet<uint> allies = new();
    private readonly HashSet<uint> partyMembers = new();
    private readonly HashSet<uint> otherPlayers = new();
    private readonly Configuration config;
    private readonly Dictionary<uint, (byte Shield, HashSet<uint> Statuses)> lastDefense = new();

    public FightTracker Tracker { get; }

    public FightService(
        GameCombatEventSource source, IFramework framework, ICondition condition, IObjectTable objectTable,
        IPartyList partyList, IDutyState dutyState, IClientState clientState, GameNames names, Configuration config)
    {
        this.config = config;
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

        // Filled on the first frame: the constructor runs off the main thread, where the object table can't be read.
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

    public string? CurrentCharacterKey =>
        objectTable.LocalPlayer is { } me
            ? $"{me.Name.TextValue} @ {me.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty}"
            : null;

    private void OnEvent(CombatEvent e) => Tracker.Handle(e);

    public bool PartyInCombat { get; private set; }

    public bool InDuty { get; private set; }

    private void OnFrameworkUpdate(IFramework _)
    {
        RefreshAllies();
        PartyInCombat = IsPartyInCombat();
        InDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
        Tracker.Update(DateTime.UtcNow, PartyInCombat);
        WatchShields();
    }

    private void WatchShields()
    {
        foreach (var id in allies)
        {
            if (objectTable.SearchByEntityId(id) is not IBattleChara member)
                continue;

            var shield = member.ShieldPercentage;
            var statuses = new HashSet<uint>();
            foreach (var status in member.StatusList)
            {
                if (status.StatusId != 0)
                    statuses.Add(status.StatusId);
            }

            if (lastDefense.TryGetValue(id, out var lastSeen) && shield < lastSeen.Shield && Tracker.Current != null)
                Tracker.Handle(new ShieldLostEvent(DateTime.UtcNow, id, lastSeen.Shield, shield, member.MaxHp));

            if (lastDefense.TryGetValue(id, out var last) && shield > last.Shield && Tracker.Current != null)
            {
                var gained = member.StatusList.FirstOrDefault(s => s.StatusId != 0 && !last.Statuses.Contains(s.StatusId));
                var sourceId = gained?.SourceId is { } src and not EffectDecoder.InvalidEntityId ? src : 0;
                Tracker.Handle(new ShieldGainedEvent(DateTime.UtcNow, id, sourceId, gained?.StatusId ?? 0, last.Shield, shield,
                    new TargetHp(member.CurrentHp, member.MaxHp), source.ReadDefense(id, 0)));
            }
            lastDefense[id] = (shield, statuses);
        }

        foreach (var gone in lastDefense.Keys.Where(k => !allies.Contains(k)).ToList())
            lastDefense.Remove(gone);
    }

    private void RefreshAllies()
    {
        allies.Clear();
        partyMembers.Clear();
        otherPlayers.Clear();
        if (objectTable.LocalPlayer is { } me)
            partyMembers.Add(me.EntityId);
        foreach (var member in partyList)
        {
            if (member.EntityId != 0)
                partyMembers.Add(member.EntityId);
        }
        allies.UnionWith(partyMembers);

        foreach (var player in objectTable.PlayerObjects)
        {
            if (player is not ICharacter character || allies.Contains(player.EntityId))
                continue;
            if (character.StatusFlags.HasFlag(StatusFlags.AllianceMember))
                allies.Add(player.EntityId);
            else
                otherPlayers.Add(player.EntityId);
        }

        Tracker.IncludeOtherPlayers = config.ShowAllPlayers;
    }

    public bool IsOtherPlayer(uint entityId) => otherPlayers.Contains(entityId);

    public bool IsPartyMember(uint entityId) =>
        partyMembers.Contains(entityId) || partyMembers.Contains(source.Actors.OwnerOf(entityId));

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
