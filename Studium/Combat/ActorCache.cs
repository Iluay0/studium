using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Studium.Core.Combat;

namespace Studium.Combat;

public sealed record ActorInfo(uint EntityId, string Name, uint ClassJobId, uint OwnerId);

/// <summary>
/// Remembers what we learned about each actor when we last saw it, so events stay readable
/// after the actor despawns (pets, adds, players leaving).
/// </summary>
public sealed class ActorCache
{
    private readonly IObjectTable objectTable;
    private readonly Dictionary<uint, ActorInfo> actors = new();

    public ActorCache(IObjectTable objectTable) => this.objectTable = objectTable;

    /// <summary>Records an actor the first time it's seen (cheap on repeat calls, which hooks make constantly).</summary>
    public void Observe(uint entityId)
    {
        if (!actors.ContainsKey(entityId))
            Refresh(entityId);
    }

    /// <summary>Re-reads an actor from the object table, e.g. to pick up a job change between fights.</summary>
    public void Refresh(uint entityId)
    {
        if (entityId == 0 || entityId == EffectDecoder.InvalidEntityId)
            return;
        if (objectTable.SearchByEntityId(entityId) is not { } obj)
            return;

        var owner = obj.OwnerId == EffectDecoder.InvalidEntityId ? 0 : obj.OwnerId;
        var job = obj is ICharacter character ? character.ClassJob.RowId : 0;
        actors[entityId] = new ActorInfo(entityId, obj.Name.TextValue, job, owner);
    }

    public ActorInfo? Get(uint entityId) => actors.GetValueOrDefault(entityId);

    public uint OwnerOf(uint entityId)
    {
        Observe(entityId);
        return actors.TryGetValue(entityId, out var info) ? info.OwnerId : 0;
    }

    public string NameOf(uint entityId) =>
        actors.TryGetValue(entityId, out var info) ? info.Name : $"#{entityId:X8}";

    public void Clear() => actors.Clear();
}
