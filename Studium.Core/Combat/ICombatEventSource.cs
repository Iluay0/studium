namespace Studium.Core.Combat;

public interface ICombatEventSource
{
    event Action<CombatEvent>? EventReceived;
}
