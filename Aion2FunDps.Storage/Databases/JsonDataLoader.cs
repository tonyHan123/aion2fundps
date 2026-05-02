using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2FunDps.Core;

namespace Aion2FunDps.Storage.Databases;

/// <summary>
/// Loads MobDatabase and SkillDatabase from JSON files in the Data/ folder
/// (copied to output directory next to the executable).
/// </summary>
public static class JsonDataLoader
{
    public static MobDatabase LoadMobDatabase(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(AppContext.BaseDirectory, "Data", "mobs.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"mobs.json not found at {path}");

        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<List<MobJsonEntry>>(stream, JsonOptions);
        if (raw == null) throw new InvalidDataException("mobs.json deserialized to null");

        return new MobDatabase(raw.Select(e => new MobInfo(e.Code, e.Name, e.Boss)));
    }

    public static SkillDatabase LoadSkillDatabase(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(AppContext.BaseDirectory, "Data", "skills.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"skills.json not found at {path}");

        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<List<SkillJsonEntry>>(stream, JsonOptions);
        if (raw == null) throw new InvalidDataException("skills.json deserialized to null");

        return new SkillDatabase(raw.Select(e => new SkillInfo(e.Code, e.Name)));
    }

    public static DungeonDatabase LoadDungeonDatabase(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(AppContext.BaseDirectory, "Data", "dungeons.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"dungeons.json not found at {path}");

        using var stream = File.OpenRead(path);
        // dungeons.json is a flat object: { "100001": "포에타", "600122": "무의 요람(보통)", ... }
        // (extracted from A2Viewer's game_db.json — see tools/extract_dungeons.py).
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        if (raw == null) throw new InvalidDataException("dungeons.json deserialized to null");

        var entries = new List<KeyValuePair<int, string>>(raw.Count);
        foreach (var (key, name) in raw)
        {
            if (int.TryParse(key, out var id)) entries.Add(new(id, name));
        }
        return new DungeonDatabase(entries);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record MobJsonEntry(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("boss")] bool Boss);

    private sealed record SkillJsonEntry(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("name")] string Name);
}
