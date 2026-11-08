using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentQuotaMonitor;

internal static class RingAppearance
{
    internal static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["codex"] = "#168f87", ["claude"] = "#c46843", ["antigravity"] = "#287bc1", ["copilot"] = "#488b74"
    };
    private static Dictionary<string, string> colors = new();
    internal static Dictionary<string, string> Copy() => new(colors);
    internal static bool Valid(string value) => Regex.IsMatch(value, "\\A#[0-9a-fA-F]{6}\\z");
    internal static void Set(IReadOnlyDictionary<string, string> value)
    {
        var next = new Dictionary<string, string>();
        foreach (var pair in value)
            if (Defaults.ContainsKey(pair.Key) && Valid(pair.Value)) next[pair.Key] = pair.Value;
        colors = next;
    }
    internal static void Apply(JsonElement preferences)
    {
        var next = new Dictionary<string, string>();
        if (preferences.TryGetProperty("mainRingColors", out var stored) && stored.ValueKind == JsonValueKind.Object)
            foreach (var pair in stored.EnumerateObject())
                if (pair.Value.ValueKind == JsonValueKind.String) next[pair.Name] = pair.Value.GetString()!;
        Set(next);
    }
    internal static string For(QuotaItem item) => item.Stale || item.Disabled ? item.Color
        : colors.TryGetValue(item.Provider, out var color) ? color : item.Color;
}
