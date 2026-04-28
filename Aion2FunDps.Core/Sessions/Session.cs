namespace Aion2FunDps.Core.Sessions;

public sealed class Session
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; private set; }
    public int? BossMobId { get; set; }

    public bool IsActive => EndedAt == null;
    public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;

    private readonly Dictionary<int, PlayerStats> _players = new();
    public IEnumerable<PlayerStats> AllPlayers => _players.Values;
    public int PlayerCount => _players.Count;
    public long TotalDamage => _players.Values.Sum(p => p.TotalDamage);

    public PlayerStats GetOrCreate(int actorId)
    {
        if (!_players.TryGetValue(actorId, out var s))
        {
            s = new PlayerStats(actorId);
            _players[actorId] = s;
        }
        return s;
    }

    public void End() => EndedAt ??= DateTime.UtcNow;
}
