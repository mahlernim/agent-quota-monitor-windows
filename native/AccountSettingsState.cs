using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AgentQuotaMonitor;

internal sealed record AccountStatus(string Id, string Provider, string Label, string Status,
    string Source, string Identity, string Error, double? LastSuccess, double? NextAttempt, string QuotaDetails = "", string RetryState = "")
{
    internal static AccountStatus[] Parse(JsonElement status)
    {
        if (!status.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw new JsonException("Account status is unavailable.");
        var result = accounts.EnumerateArray().Select(account => new AccountStatus(
            Text(account, "id"), Text(account, "provider"), Text(account, "label"), Text(account, "status"),
            Text(account, "source"), Text(account, "identityStatus"), Text(account, "error"),
            Number(account, "lastSuccess"), Number(account, "nextAttempt"), DescribeQuotas(account), Text(account, "retryState"))).ToArray();
        if (result.Any(account => string.IsNullOrWhiteSpace(account.Id)) ||
            result.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Account identities are invalid.");
        return result;
    }

    internal static string Timestamp(double? seconds, string fallback)
    {
        if (seconds is null || !double.IsFinite(seconds.Value)) return fallback;
        try { return DateTimeOffset.UnixEpoch.AddSeconds(seconds.Value).LocalDateTime.ToString("g", CultureInfo.CurrentCulture); }
        catch (ArgumentOutOfRangeException) { return fallback; }
    }

    internal string Guidance => RetryState == "suspended"
        ? "Automatic reads are paused because the provider reported an unusable retry time. Check the official client for status."
        : Error switch
    {
        "" => "",
        "sign_in_required" or "local_session_unavailable" when IsAntigravityCli =>
            "Antigravity CLI session needs attention. Run agy interactively and sign in if prompted, then run agy -p /usage and press Refresh in the monitor.",
        "sign_in_required" when IsAntigravityDesktop =>
            "Antigravity desktop session was rejected. Sign in through the Antigravity desktop app, then press Refresh in the monitor.",
        "local_session_unavailable" when IsAntigravityDesktop =>
            "Antigravity desktop session is unavailable. Open the Antigravity desktop app, then press Refresh in the monitor.",
        "sign_in_required" when Provider == "claude" =>
            "Claude quota read was rejected. Check claude auth status, open Claude Code, then press Refresh. If Claude Code reports an expired login or the read still fails after renewal, sign in through Claude Code.",
        "sign_in_required" => "Session expired or rejected. Sign in through the official client.",
        "local_session_unavailable" => "Local session unavailable.",
        "independent_sign_in_needed" => "Official sign-in is needed.",
        "rate_limited" => "Provider rate limit. Waiting until the next eligible read.",
        "identity_changed" => "Account changed. Waiting for discovery.",
        "quota_not_reported" => "No quota was reported.",
        "copilot_setup_required" => "Run Setup-Copilot.ps1 to verify the existing GitHub CLI account.",
        "copilot_auth_source_unsupported" => "The existing github.com GitHub CLI sign-in is required.",
        "copilot_read_timeout" => "Copilot quota read timed out.",
        "antigravity_cli_timeout" => "Antigravity CLI quota read timed out. Waiting to retry.",
        "antigravity_cli_failed" => "Run agy interactively and sign in if prompted, then run agy -p /usage and press Refresh in the monitor.",
        "antigravity_cli_auth_unsupported" => "Antigravity CLI has a custom provider, API key, or unreadable auth settings. Restore Google account sign-in in agy, then run agy -p /usage and press Refresh in the monitor.",
        _ => "Quota reader unavailable. " + Error.Replace('_', ' ')
    };

    private bool IsAntigravityCli => Provider == "antigravity" &&
        Source.StartsWith("Official Antigravity CLI", StringComparison.Ordinal);
    private bool IsAntigravityDesktop => Provider == "antigravity" &&
        Source == "Official running Antigravity local service";

    private static string DescribeQuotas(JsonElement account)
    {
        if (!account.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array) return "";
        var lines = new List<string>();
        foreach (JsonElement group in groups.EnumerateArray())
        {
            string groupLabel = Text(group, "label");
            string plan = Text(group, "plan");
            if (plan.Length > 0) lines.Add("Plan · " + plan);
            if (group.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            {
                string[] names = models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.String).Select(model => model.GetString() ?? "").ToArray();
                lines.Add("Models · " + (names.Length > 0 ? string.Join(", ", names) : "Not reported"));
            }
            if (!group.TryGetProperty("buckets", out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                var parts = new List<string>();
                double? amount = Number(bucket, "amountRemaining"), entitlement = Number(bucket, "entitlement"), used = Number(bucket, "used");
                string unit = Text(bucket, "unit");
                if (amount.HasValue || entitlement.HasValue)
                    parts.Add($"{Amount(amount)} / {Amount(entitlement)} {unit} remaining".Replace("  ", " "));
                if (used.HasValue) parts.Add($"{Amount(used)} {unit} used".Replace("  ", " "));
                if (bucket.TryGetProperty("available", out JsonElement available) && available.ValueKind is JsonValueKind.False or JsonValueKind.True)
                    parts.Add(available.ValueKind == JsonValueKind.True ? "Currently available" : "Currently unavailable");
                if (parts.Count > 0) lines.Add($"{groupLabel} · {Text(bucket, "label")} · {string.Join(" · ", parts)}");
            }
        }
        return string.Join("\n", lines.Distinct(StringComparer.Ordinal));
    }

    private static string Amount(double? value) => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "Not reported";

    private static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static double? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
}

internal sealed class AccountLayoutState
{
    private AccountStatus[] _latest = [];
    private Dictionary<string, AccountStatus> _baseline = new(StringComparer.Ordinal);
    private List<string> _draft = [];
    private bool _serverConflict;
    internal bool Loaded { get; private set; }
    internal bool Editing { get; private set; }
    internal bool HasConflict => Editing && (_serverConflict || !_baseline.Keys.ToHashSet(StringComparer.Ordinal)
        .SetEquals(_latest.Select(account => account.Id)));
    internal string[] Order => _draft.ToArray();
    internal IReadOnlyList<AccountStatus> Displayed
    {
        get
        {
            if (!Editing) return _latest;
            var current = _latest.ToDictionary(account => account.Id, StringComparer.Ordinal);
            return _draft.Select(id => current.GetValueOrDefault(id) ?? _baseline[id]).ToArray();
        }
    }
    internal void Observe(AccountStatus[] accounts) { _latest = accounts; Loaded = true; }
    internal void Begin()
    {
        if (!Loaded || Editing) return;
        _baseline = _latest.ToDictionary(account => account.Id, StringComparer.Ordinal);
        _draft = _latest.Select(account => account.Id).ToList();
        _serverConflict = false;
        Editing = true;
    }
    internal bool Move(string id, int direction)
    {
        if (!Editing || HasConflict || direction is not (-1 or 1)) return false;
        int from = _draft.IndexOf(id), to = from + direction;
        if (from < 0 || to < 0 || to >= _draft.Count) return false;
        (_draft[from], _draft[to]) = (_draft[to], _draft[from]);
        return true;
    }
    internal void Reject() => _serverConflict = true;
    internal void Commit()
    {
        _latest = Displayed.ToArray();
        Cancel();
    }
    internal void Cancel()
    {
        Editing = false;
        _serverConflict = false;
        _draft.Clear();
        _baseline.Clear();
    }
}
