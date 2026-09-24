namespace Studium.Core.Combat;

/// <summary>
/// Where combat events come from. The game hooks implement this; tests feed synthetic events.
/// </summary>
public interface ICombatEventSource
{
    event Action<CombatEvent>? EventReceived;
}
