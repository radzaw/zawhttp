using System.Text.Json;

namespace ZawHTTP;

/// <summary>
/// Persists the workspace in the configuration directory: collections, environments and UI state as
/// documents owned by the UI, and the request history, which the backend appends to on every send.
/// </summary>
public sealed class Workspace
{
    public const int MaxHistory = 500;
    readonly string dir;
    readonly object gate = new();
    readonly List<HistoryEntry> history;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string[] Documents = ["state", "collections", "environments"];

    public Workspace(AppOptions o)
    {
        dir = o.ConfigDir;
        history = LoadHistory();
    }

    public static bool IsDocument(string name) => Documents.Contains(name);

    /// <summary>A stored document, or null (JSON) when there is none yet.</summary>
    public JsonElement Load(string name)
    {
        var path = Path.Combine(dir, name + ".json");
        lock (gate)
        {
            if (!File.Exists(path)) return default;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Keep the damaged file for the user and start over rather than refusing to open.
                File.Copy(path, path + ".broken", overwrite: true);
                return default;
            }
        }
    }

    public void Save(string name, JsonElement doc)
    {
        lock (gate) WriteAtomic(Path.Combine(dir, name + ".json"), doc.GetRawText());
    }

    public List<HistoryEntry> History()
    {
        lock (gate) return [.. history];
    }

    public void AddHistory(HistoryEntry e)
    {
        lock (gate)
        {
            history.Insert(0, e);
            if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
            SaveHistory();
        }
    }

    public void DeleteHistory(string? id)
    {
        lock (gate)
        {
            if (id == null) history.Clear();
            else history.RemoveAll(h => h.Id == id);
            SaveHistory();
        }
    }

    List<HistoryEntry> LoadHistory()
    {
        var path = Path.Combine(dir, "history.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void SaveHistory() => WriteAtomic(Path.Combine(dir, "history.json"), JsonSerializer.Serialize(history, Json));

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
