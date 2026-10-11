using System.Globalization;
using System.Text.RegularExpressions;

namespace ZawHTTP;

/// <summary>
/// Resolves {{name}} placeholders from environment and global variables, plus a few dynamic ones
/// ({{$guid}}, {{$timestamp}}, {{$isoTimestamp}}, {{$randomInt}}). Unknown names are left as they are
/// and collected in <see cref="Unresolved"/> so the UI can warn about them.
/// </summary>
public sealed partial class Variables
{
    readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public SortedSet<string> Unresolved { get; } = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{\s*([^{}\s][^{}]*?)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Enabled entries with a name; a later entry overrides an earlier one (globals first, then the environment).</summary>
    public Variables(IEnumerable<KeyValue>? vars)
    {
        foreach (var v in vars ?? [])
            if (v.Enabled && !string.IsNullOrWhiteSpace(v.Key)) values[v.Key.Trim()] = v.Value ?? "";
    }

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{")) return text ?? "";
        // A variable's value may itself refer to variables; a few passes cover that without looping forever.
        for (var pass = 0; pass < 5 && text.Contains("{{"); pass++)
        {
            var next = Placeholder().Replace(text, m => Lookup(m.Groups[1].Value, out var v) ? v : m.Value);
            if (next == text) break;
            text = next;
        }
        foreach (Match m in Placeholder().Matches(text))
            if (!Lookup(m.Groups[1].Value, out _)) Unresolved.Add(m.Groups[1].Value);
        return text;
    }

    bool Lookup(string name, out string value)
    {
        if (values.TryGetValue(name, out value!)) return true;
        switch (name)
        {
            case "$guid" or "$randomUUID": value = Guid.NewGuid().ToString(); return true;
            case "$timestamp": value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture); return true;
            case "$isoTimestamp": value = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture); return true;
            case "$randomInt": value = Random.Shared.Next(0, 1001).ToString(CultureInfo.InvariantCulture); return true;
        }
        value = "";
        return false;
    }
}
