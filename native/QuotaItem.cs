using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AgentQuotaMonitor;

public sealed class QuotaItem
{
    public string Key { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string BucketId { get; init; } = "";
    public string Provider { get; init; } = "";
    /// <summary>Internal quota code. It selects the identity color and is never user-editable.</summary>
    public string Code { get; init; } = "";
    /// <summary>Quota type for display names, derived from the internal code when not set.</summary>
    public string Type
    {
        get => type.Length > 0 ? type : Code switch
        {
            "CX" => "codex", "CL" => "claude", "GM" => "antigravity-gemini", "CG" => "antigravity-claude-gpt", "CP" => "copilot", _ => ""
        };
        init => type = value;
    }
    private readonly string type = "";
    public string Initials => QuotaNames.For(Type).Initials;
    public string Name => QuotaNames.For(Type).Name;
    /// <summary>Readable ring label, such as "Codex 5h". Copilot pools keep their pool names.</summary>
    public string Label => Provider == "copilot"
        ? Group.Contains("Inline") ? "Inline" : Group.Contains("Premium") ? "Premium" : "Included"
        : Name + " " + QuotaNames.ShortWindow(Window);
    /// <summary>Compact label for tight spaces, such as "AG 5h".</summary>
    public string ShortLabel => Initials + " " + QuotaNames.ShortWindow(Window);
    public string Account { get; init; } = "";
    public string Group { get; init; } = "";
    public string Window { get; init; } = "";
    public string Status { get; set; } = "pending";
    private string resetText = "Reset unknown", tooltip = "", weeklyResetText = "", weeklyTooltip = "";
    public string ResetText { get => WeeklyLimitReached ? weeklyResetText : resetText; init => resetText = value; }
    public string Tooltip { get => WeeklyLimitReached ? weeklyTooltip : tooltip; set => tooltip = value; }
    public double? Remaining { get; init; }
    public double? TimeRemaining { get; set; }
    public bool Unlimited { get; init; }
    public bool Disabled { get; init; }
    // Effective baseline availability is separate from the provider's disabled/null reading.
    public bool WeeklyLimitReached => !Stale && weeklyResetText.Length > 0;
    public bool Stale => Status != "live";
    public void MarkDisconnected() { Status = "stale"; TimeRemaining = null; Tooltip = "Disconnected · cached quota\n" + Tooltip.Replace("Disconnected · cached quota\n", ""); }
    public string IdentityColor => Code switch { "CX" => "#168f87", "CL" => "#c46843", "GM" => "#287bc1", "CG" => "#99734b", "CP" => "#488b74", _ => "#287bc1" };
    public string Color => Stale || Disabled ? "#89929d" : IdentityColor;
    public string NumberColor => Stale || Disabled ? "#89929d" : TimeRemaining is > 0 && Remaining.HasValue
        ? Remaining < TimeRemaining / 4 ? "#c54444" : Remaining < TimeRemaining / 2 ? "#c18a19" : "#25313d"
        : "#25313d";
    public object Selection => new { accountId = AccountId, groupId = GroupId, bucketId = BucketId };
    public static string Percent(double value) => value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
    public static string MakeKey(string a, string g, string b) => JsonSerializer.Serialize(new[] { a, g, b });
    public static string Text(JsonElement value, string key, string fallback = "") =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    public static double? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.TryGetDoubleSafe(out var number) && double.IsFinite(number) ? number : null;
    internal static DateTimeOffset? ExhaustedWeeklyReset(JsonElement account, JsonElement group, JsonElement bucket, DateTimeOffset now)
    {
        static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
        if (Text(account, "provider") != "antigravity" || Text(account, "status") != "live" || Text(account, "error").Length > 0 ||
            !Flag(bucket, "disabled") || Number(bucket, "windowSeconds") != 18000) return null;
        // Match the backend's ten-minute freshness limit, including snapshots with an old live flag.
        var success = Number(account, "lastSuccess");
        var age = (now - DateTimeOffset.UnixEpoch).TotalSeconds - success;
        if (age is null or < 0 or > 600) return null;
        JsonElement weekly = default;
        foreach (var candidate in group.GetProperty("buckets").EnumerateArray())
        {
            if (Number(candidate, "windowSeconds") != 604800) continue;
            if (weekly.ValueKind != JsonValueKind.Undefined) return null;
            weekly = candidate;
        }
        if (weekly.ValueKind == JsonValueKind.Undefined || Flag(weekly, "disabled") || Flag(weekly, "unlimited") ||
            Number(weekly, "remaining") != 0 || !DateTimeOffset.TryParse(Text(weekly, "resetsAt"), out var reset) ||
            reset <= now || (reset - now).TotalSeconds > 604800) return null;
        return reset;
    }
    internal static string DescribeWeeklyReset(DateTimeOffset reset, DateTimeOffset now)
    {
        var left = reset - now;
        return $"Weekly reset in {(int)left.TotalHours}h {left.Minutes}m · {reset.LocalDateTime:g}";
    }
    public static List<QuotaItem> Parse(JsonElement root, DateTimeOffset? now = null)
    {
        var observedAt = now ?? DateTimeOffset.UtcNow;
        var result = new List<QuotaItem>();
        if (!root.TryGetProperty("accounts", out var accounts)) return result;
        foreach (var a in accounts.EnumerateArray())
        foreach (var g in a.GetProperty("groups").EnumerateArray())
        foreach (var b in g.GetProperty("buckets").EnumerateArray())
        {
            var guidance = AccountStatus.From(a).Guidance; var note = guidance.Length > 0 ? "\n" + guidance : "";
            var provider = Text(a, "provider"); var group = Text(g, "label"); var status = Text(a, "status");
            var code = provider switch { "codex" => "CX", "claude" => "CL", "copilot" => "CP",
                "antigravity" => group.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "GM" : "CG", _ => "?" };
            var disabled = b.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
            var remaining = disabled ? null : Number(b, "remaining"); if (remaining is < 0 or > 100) remaining = null;
            var duration = Number(b, "windowSeconds");
            var window = duration == 18000 ? "5h" : duration == 604800 ? "7d" : Text(b, "windowKind") == "monthly" ? "Month" : Text(b, "label");
            var unlimited = !disabled && b.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
            double? time = null; var resetText = disabled ? "Window disabled by provider" : "Reset unknown";
            if (!disabled && DateTimeOffset.TryParse(Text(b, "resetsAt"), out var reset))
            {
                var left = reset - observedAt;
                resetText = left.TotalSeconds > 0 ? $"Resets in {(int)left.TotalHours}h {left.Minutes}m · {reset.LocalDateTime:g}" : "Reset due · awaiting provider";
                if (status != "live" && left.TotalSeconds <= 0 && !unlimited)
                {
                    // The cached value predates the reset, so it no longer describes this window.
                    remaining = null;
                    resetText = "Reset since the last read · waiting for a fresh read";
                }
                if (status == "live" && remaining.HasValue && duration is 18000 or 604800 && left.TotalSeconds > 0 && left.TotalSeconds <= duration &&
                    !(b.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False)) time = left.TotalSeconds / duration * 100;
            }
            var aid = Text(a, "id"); var gid = Text(g, "id"); var bid = Text(b, "id");
            var quotaText = disabled ? "Disabled" : unlimited ? "Unlimited" : remaining.HasValue ? Percent(remaining.Value) + " remaining" : "Unknown";
            var pace = time.HasValue ? $"\n{Percent(time.Value)} time left · {(remaining >= time ? "Within pace" : "Faster usage")}" : "";
            var weeklyReset = ExhaustedWeeklyReset(a, g, b, observedAt);
            var weeklyText = weeklyReset.HasValue ? DescribeWeeklyReset(weeklyReset.Value, observedAt) : "";
            result.Add(new QuotaItem { Key = MakeKey(aid, gid, bid), AccountId = aid, GroupId = gid, BucketId = bid,
                Provider = provider, Code = code, Type = QuotaNames.TypeFor(provider, group), Account = Text(a, "label"), Group = group, Window = window,
                Status = status, Remaining = remaining, Unlimited = unlimited, Disabled = disabled, TimeRemaining = time, ResetText = resetText,
                weeklyResetText = weeklyText,
                weeklyTooltip = $"{provider} · {Text(a, "label")}\n{group} · {window} · 0% available · Weekly limit reached\n{status.ToUpperInvariant()} · {weeklyText}\nBaseline quota only · AI Credit overages may allow continued use{note}",
                Tooltip = $"{provider} · {Text(a, "label")}\n{group} · {window} · {quotaText}\n{status.ToUpperInvariant()} · {resetText}{pace}{note}" });
        }
        return result;
    }
}
internal static class JsonNumbers
{
    internal static bool TryGetDoubleSafe(this JsonElement element, out double value)
    { value = 0; return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value); }
}
