namespace Aion2FunDps.Core.Sessions;

public sealed class Session
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; private set; }
    public int? BossMobId { get; set; }

    public bool IsActive => EndedAt == null;
    public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;

    // ConcurrentDictionary so the UI thread can enumerate AllPlayers safely
    // while the capture thread is mutating via GetOrCreate / Remove. Plain
    // Dictionary throws "Collection was modified" if the UI hits an Add
    // mid-enumeration, which silently truncates the leaderboard for that
    // tick and reproduces as "joiner doesn't show until next event reshuffles".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, PlayerStats> _players = new();
    public IEnumerable<PlayerStats> AllPlayers => _players.Values;
    public int PlayerCount => _players.Count;
    public long TotalDamage => _players.Values.Sum(p => p.TotalDamage);

    public PlayerStats GetOrCreate(int actorId) =>
        _players.GetOrAdd(actorId, id => new PlayerStats(id));

    public PlayerStats? GetExisting(int actorId) =>
        _players.TryGetValue(actorId, out var s) ? s : null;

    public bool Remove(int actorId) => _players.TryRemove(actorId, out _);

    public void End() => EndedAt ??= DateTime.UtcNow;

    public void RebaseStart(DateTime startedAt)
    {
        StartedAt = startedAt;
        EndedAt = null;
    }
}
