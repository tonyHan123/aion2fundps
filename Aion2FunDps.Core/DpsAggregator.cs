using Aion2FunDps.Core.Models;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.Core;

/// <summary>
/// Consumes IGameEvents and maintains current Session + per-player stats + nickname registry.
/// Single-threaded — caller serializes events.
/// </summary>
public sealed class DpsAggregator
{
    private readonly NicknameRegistry _registry;
    public Session Current { get; private set; }
    public NicknameRegistry Registry => _registry;

    public long DamageEventCount { get; private set; }
    public long HpEventCount { get; private set; }
    public long NicknameEventCount { get; private set; }
    public long CombatBoundaryEventCount { get; private set; }

    public DpsAggregator()
    {
        _registry = new NicknameRegistry();
        Current = new Session();
    }

    public void OnEvent(IGameEvent evt)
    {
        switch (evt)
        {
            case DamageEvent dmg:
                Current.GetOrCreate(dmg.ActorId).Apply(dmg);
                DamageEventCount++;
                break;

            case NicknameInfo nick:
                _registry.Register(nick);
                NicknameEventCount++;
                break;

            case CombatBoundary cb:
                CombatBoundaryEventCount++;
                // Future: end current session if boss dies, start new on engage.
                // Current session model is single-session for v1d simplicity.
                break;

            case MobHpUpdate:
                HpEventCount++;
                // Future: cross-check sum(damage) vs HP delta in Phase 1e.
                break;
        }
    }

    /// <summary>Force-end current session and start a fresh one.</summary>
    public void Reset()
    {
        Current.End();
        Current = new Session();
    }
}
