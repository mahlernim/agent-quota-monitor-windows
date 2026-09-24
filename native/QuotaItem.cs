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
    public string Code { get; init; } = "";
    public string Account { get; init; } = "";
    public string Group { get; init; } = "";
    public string Window { get; init; } = "";
    public string Status { get; set; } = "pending";
    public string ResetText { get; init; } = "Reset unknown";
    public string Tooltip { get; set; } = "";
    public double? Remaining { get; init; }
    public double? TimeRemaining { get; set; }
    public bool Unlimited { get; init; }
    public bool Stale => Status != "live";
    public void MarkDisconnected() { Status = "stale"; TimeRemaining = null; Tooltip = "Disconnected · cached quota\n" + Tooltip.Replace("Disconnected · cached quota\n", ""); }
    public string IdentityColor => Code switch { "CX" => "#168f87", "CL" => "#c46843", "GM" => "#287bc1", "CG" => "#99734b", "CP" => "#488b74", _ => "#287bc1" };
    public string Color => Stale ? "#89929d" : IdentityColor;
    public string NumberColor => Stale ? "#89929d" : TimeRemaining is > 0 && Remaining.HasValue
        ? Remaining < TimeRemaining / 4 ? "#c54444" : Remaining < TimeRemaining / 2 ? "#c18a19" : "#25313d"
        : "#25313d";
    public object Selection => new { accountId = AccountId, groupId = GroupId, bucketId = BucketId };
    public static string Percent(double value) => value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
    public static string MakeKey(string a, string g, string b) => JsonSerializer.Serialize(new[] { a, g, b });
    public static string Text(JsonElement value, string key, string fallback = "") =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    public static double? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.TryGetDoubleSafe(out var number) && double.IsFinite(number) ? number : null;
    public static List<QuotaItem> Parse(JsonElement root, DateTimeOffset? now = null)
    {
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
            var remaining = Number(b, "remaining"); if (remaining is < 0 or > 100) remaining = null;
            var duration = Number(b, "windowSeconds");
            var window = duration == 18000 ? "5h" : duration == 604800 ? "7d" : Text(b, "windowKind") == "monthly" ? "Month" : Text(b, "label");
            var unlimited = b.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
            double? time = null; var resetText = "Reset unknown";
            if (DateTimeOffset.TryParse(Text(b, "resetsAt"), out var reset))
            {
                var left = reset - (now ?? DateTimeOffset.UtcNow);
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
            var quotaText = unlimited ? "Unlimited" : remaining.HasValue ? Percent(remaining.Value) + " remaining" : "Unknown";
            var pace = time.HasValue ? $"\n{Percent(time.Value)} time left · {(remaining >= time ? "Within pace" : "Faster usage")}" : "";
            result.Add(new QuotaItem { Key = MakeKey(aid, gid, bid), AccountId = aid, GroupId = gid, BucketId = bid,
                Provider = provider, Code = code, Account = Text(a, "label"), Group = group, Window = window,
                Status = status, Remaining = remaining, Unlimited = unlimited, TimeRemaining = time, ResetText = resetText,
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
