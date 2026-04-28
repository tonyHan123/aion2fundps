using System.Collections.Concurrent;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core;

public sealed class NicknameRegistry
{
    private readonly ConcurrentDictionary<int, NicknameEntry> _entries = new();
    public int? SelfUserId { get; private set; }

    public void Register(NicknameInfo info)
    {
        _entries[info.UserId] = new NicknameEntry(info.Nickname, info.IsSelf, info.Server, info.Job);
        if (info.IsSelf) SelfUserId = info.UserId;
    }

    public string? GetName(int userId) =>
        _entries.TryGetValue(userId, out var e) ? e.Nickname : null;

    public NicknameEntry? GetEntry(int userId) =>
        _entries.TryGetValue(userId, out var e) ? e : null;

    public IEnumerable<KeyValuePair<int, NicknameEntry>> All => _entries;
}

public sealed record NicknameEntry(string Nickname, bool IsSelf, int Server, int Job);
