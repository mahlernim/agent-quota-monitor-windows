using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AgentQuotaMonitor;

internal sealed record QuotaName(string Initials, string Name);

/// <summary>
/// User-editable display initials and names per quota type. Presentation only: the internal
/// code still selects ring colors, and account, group, and bucket IDs still identify rings.
/// </summary>
internal static class QuotaNames
{
    internal const int MaxInitials = 3;
    internal const int MaxName = 16;
    internal static readonly string[] Types = ["codex", "claude", "antigravity-gemini", "antigravity-claude-gpt", "copilot"];
    internal static readonly IReadOnlyDictionary<string, string> TypeLabels = new Dictionary<string, string>
    {
        ["codex"] = "OpenAI Codex", ["claude"] = "Anthropic Claude", ["antigravity-gemini"] = "Antigravity Gemini",
        ["antigravity-claude-gpt"] = "Antigravity Claude and GPT", ["copilot"] = "GitHub Copilot"
    };
    internal static readonly IReadOnlyDictionary<string, QuotaName> Defaults = new Dictionary<string, QuotaName>
    {
        ["codex"] = new("CX", "Codex"), ["claude"] = new("CL", "Claude"), ["antigravity-gemini"] = new("AG", "Gemini"),
        ["antigravity-claude-gpt"] = new("AC", "Claude and GPT"), ["copilot"] = new("CP", "Copilot")
    };
    private static volatile IReadOnlyDictionary<string, QuotaName> current = Defaults;
    internal static IReadOnlyDictionary<string, QuotaName> Current => current;

    internal static string TypeFor(string provider, string group) => provider switch
    {
        "codex" or "claude" or "copilot" => provider,
        "antigravity" => group.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "antigravity-gemini" : "antigravity-claude-gpt",
        _ => ""
    };

    internal static QuotaName For(string type) =>
        current.TryGetValue(type, out QuotaName? name) ? name : Defaults.GetValueOrDefault(type) ?? new QuotaName("?", "Quota");

    internal static bool ValidInitials(string value) =>
        value.Length is >= 1 and <= MaxInitials && value.All(char.IsLetterOrDigit);

    internal static bool ValidName(string value) =>
        value.Length is >= 1 and <= MaxName && value == value.Trim() && !value.Any(char.IsControl);

    internal static void Set(IReadOnlyDictionary<string, QuotaName> names)
    {
        var merged = Defaults.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach ((string type, QuotaName name) in names)
            if (merged.ContainsKey(type) && ValidInitials(name.Initials) && ValidName(name.Name)) merged[type] = name;
        current = merged;
    }

    /// <summary>Reads saved names from desktop preferences, ignoring anything invalid.</summary>
    internal static void Apply(JsonElement preferences)
    {
        var names = new Dictionary<string, QuotaName>();
        if (preferences.ValueKind == JsonValueKind.Object && preferences.TryGetProperty("quotaLabels", out JsonElement saved) &&
            saved.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty entry in saved.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.TryGetProperty("initials", out JsonElement initials) && initials.ValueKind == JsonValueKind.String &&
                    entry.Value.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    names[entry.Name] = new QuotaName(initials.GetString()!, name.GetString()!);
        Set(names);
    }

    internal static Dictionary<string, object> ToPreference(IReadOnlyDictionary<string, QuotaName> names) =>
        names.Where(pair => Types.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (object)new { initials = pair.Value.Initials, name = pair.Value.Name });

    /// <summary>Short window text used next to a name, such as "5h" or "7d".</summary>
    internal static string ShortWindow(string window) => window switch
    {
        "5h" or "7d" => window,
        "Month" => "month",
        _ => window.ToLower(CultureInfo.CurrentCulture)
    };
}
