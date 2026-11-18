using System.Text.Json;

namespace BugFixer;

public sealed record BugRecord(
    int Id, string Title, string Outcome, DateTimeOffset At,
    string? Branch = null, string? PrUrl = null, string? Note = null);

/// <summary>Every bug the loop has touched, so it never picks the same one twice.</summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly Dictionary<int, BugRecord> _bugs;

    private StateStore(string path, Dictionary<int, BugRecord> bugs) { _path = path; _bugs = bugs; }

    public static StateStore Load(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        var path = Path.Combine(stateDir, "state.json");
        var list = File.Exists(path)
            ? JsonSerializer.Deserialize<List<BugRecord>>(File.ReadAllText(path), Json.Options) ?? new()
            : new List<BugRecord>();
        return new StateStore(path, list.ToDictionary(b => b.Id));
    }

    public IReadOnlyCollection<int> ProcessedIds => _bugs.Keys;
    public bool Contains(int id) => _bugs.ContainsKey(id);

    public void Record(BugRecord rec)
    {
        _bugs[rec.Id] = rec;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_bugs.Values.OrderBy(b => b.At).ToList(), Json.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Lets you re-queue a bug: `bugfixer --forget 1234`.</summary>
    public bool Forget(int id)
    {
        if (!_bugs.Remove(id)) return false;
        File.WriteAllText(_path, JsonSerializer.Serialize(_bugs.Values.OrderBy(b => b.At).ToList(), Json.Options));
        return true;
    }

    public IEnumerable<BugRecord> All => _bugs.Values.OrderBy(b => b.At);
}
