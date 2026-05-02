namespace Aion2FunDps.Core;

/// <summary>
/// Static lookup of Aion 2 dungeon ids → display name. Loaded from
/// dungeons.json (extracted from A2Viewer's game_db.json), covering both
/// matchmaking expedition rooms (e.g., "무의 요람(보통)") and zone-load
/// areas. The display name already encodes difficulty in parentheses, so
/// no separate stage handling is required at the UI layer.
/// </summary>
public sealed class DungeonDatabase
{
    private readonly Dictionary<int, string> _byId;

    public DungeonDatabase(IEnumerable<KeyValuePair<int, string>> entries)
    {
        _byId = new Dictionary<int, string>();
        foreach (var (id, name) in entries) _byId[id] = name;
    }

    public int Count => _byId.Count;

    public string? GetName(int dungeonId) =>
        _byId.TryGetValue(dungeonId, out var n) ? n : null;
}
