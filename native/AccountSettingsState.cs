using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AgentQuotaMonitor;

internal sealed record AccountProblem(string Summary, string? Action = null, string? ActionLabel = null);

internal sealed record AccountStatus(string Id, string Provider, string Label, string Status,
    string Source, string Identity, string Error, double? LastSuccess, double? NextAttempt, string QuotaDetails = "", string RetryState = "",
    double? SessionExpiresAt = null, double? SessionRenewedAt = null)
{
    internal static AccountStatus[] Parse(JsonElement status)
    {
        if (!status.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw new JsonException("Account status is unavailable.");
        var result = accounts.EnumerateArray().Select(From).ToArray();
        if (result.Any(account => string.IsNullOrWhiteSpace(account.Id)) ||
            result.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Account identities are invalid.");
        return result;
    }

    internal static AccountStatus From(JsonElement account) => new(
        Text(account, "id"), Text(account, "provider"), Text(account, "label"), Text(account, "status"),
        Text(account, "source"), Text(account, "identityStatus"), Text(account, "error"),
        Number(account, "lastSuccess"), Number(account, "nextAttempt"), DescribeQuotas(account), Text(account, "retryState"),
        Number(account, "sessionExpiresAt"), Number(account, "sessionRenewedAt"));

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
        "sign_in_required" when Provider == "codex" =>
            "Codex did not accept the saved session. Open the Codex app or CLI to renew it. The monitor resumes by itself afterward. Sign in again only if that does not help.",
        "sign_in_required" => "Session expired or rejected. Sign in through the official client.",
        "session_expired" when Provider == "claude" =>
            "The Claude Code session expired. An open but idle Claude Code renews it only when used, so send any message in Claude Code. " +
            "The monitor resumes by itself. Sign in again only if Claude Code reports that you are signed out.",
        "client_not_installed" when Provider == "codex" =>
            "Codex isn't installed on this computer. Install it from Settings or the main window, then choose Sign in.",
        "client_not_installed" when Provider == "claude" =>
            "Claude Code isn't installed on this computer. Install it from Settings or the main window, then choose Sign in.",
        "session_expired" => "The official session expired. Open the official client to renew it.",
        "antigravity_cli_unavailable" =>
            "The Antigravity CLI (agy) is not installed and the Antigravity desktop app is closed. Install the official CLI from Settings to read quota without the desktop app, or open the desktop app. Gemini CLI does not report Antigravity quota.",
        "network_unavailable" => "No network connection. The monitor retries every five minutes and again when the network returns.",
        "connection_or_response_error" => "The provider could not be reached or returned an unreadable response. Waiting to retry.",
        "schema_changed" => "The provider changed its response format. Check for a monitor update in Settings.",
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

    /// <summary>
    /// A one-sentence summary and at most one action for the main window. The action depends on
    /// provider and source as well as the error, so CLI accounts never get a desktop action.
    /// </summary>
    internal AccountProblem? Problem
    {
        get
        {
            if (RetryState == "suspended") return new("Automatic reads are paused by the provider's retry time.");
            bool official = Provider is "codex" or "claude" or "copilot";
            return Error switch
            {
                "" => null,
                "sign_in_required" or "local_session_unavailable" or "antigravity_cli_failed" when IsAntigravityCli =>
                    new("The Antigravity CLI needs sign-in. Run agy -p /usage in a terminal.", "copy-agy", "Copy command"),
                "antigravity_cli_unavailable" => new("The Antigravity CLI isn't installed and the desktop app is closed.", "install-cli", "Install CLI"),
                "local_session_unavailable" when IsAntigravityDesktop => new("The Antigravity desktop app is closed.", "open-desktop", "Open desktop app"),
                "sign_in_required" when IsAntigravityDesktop => new("The Antigravity desktop session was rejected.", "open-desktop", "Open desktop app"),
                "session_expired" when Provider == "claude" => new("Your Claude Code session expired. Send any message in Claude Code to renew it."),
                "client_not_installed" when Provider == "codex" => new("Codex isn't installed.", "install-codex", "Install Codex"),
                "client_not_installed" when Provider == "claude" => new("Claude Code isn't installed.", "install-claude", "Install Claude Code"),
                "session_expired" => new("The session expired. Open the official client to renew it."),
                "sign_in_required" when Provider == "codex" => new("Codex didn't accept the saved session. Open Codex, or sign in again.", "sign-in", "Sign in"),
                "sign_in_required" when Provider == "claude" => new("Claude didn't accept the saved session. Open Claude Code, or sign in again.", "sign-in", "Sign in"),
                "sign_in_required" when official => new("The official client needs sign-in.", "sign-in", "Sign in"),
                "local_session_unavailable" when official => new("No readable session from the official client.", "sign-in", "Sign in"),
                "network_unavailable" => new("No network connection.", "retry", "Retry"),
                "connection_or_response_error" => new("The provider couldn't be reached.", "retry", "Retry"),
                "schema_changed" => new("The provider changed its data format.", "check-updates", "Check for updates"),
                "rate_limited" => new("Provider rate limit. Waiting for the next read."),
                _ => new(Guidance.Split(". ")[0].TrimEnd('.') + ".")
            };
        }
    }

    /// <summary>The reading source in plain words.</summary>
    internal string SourceText => Source switch
    {
        "Official Codex session / usage endpoint" => "Codex app or CLI session",
        "Official Claude Code session / OAuth usage endpoint" => "Claude Code session",
        "Official running Antigravity local service" => "Antigravity desktop app",
        _ when IsAntigravityCli => "Antigravity CLI (agy)",
        _ when Provider == "copilot" && Source.Length > 0 => "GitHub CLI and Copilot SDK",
        "" => "Not reported",
        _ => Source
    };

    /// <summary>
    /// Support text built from an explicit field list. It never contains credentials, and it
    /// leaves out the account label because that is often an email address.
    /// </summary>
    internal string Diagnostics() => string.Join(Environment.NewLine, new[]
    {
        "Agent Quota Monitor " + UpdateService.InstalledVersion,
        "Provider · " + Provider,
        "Status · " + (Status.Length > 0 ? Status : "unknown"),
        "Error · " + (Error.Length > 0 ? Error : "none"),
        "Retry state · " + (RetryState.Length > 0 ? RetryState : "normal"),
        "Source · " + (Source.Length > 0 ? Source : "not reported"),
        "Identity · " + (Identity.Length > 0 ? Identity : "not reported"),
        "Account ID · " + Id,
        "Last successful read · " + Timestamp(LastSuccess, "never"),
        "Next eligible read · " + Timestamp(NextAttempt, "not reported"),
        "Session expires · " + Timestamp(SessionExpiresAt, "not reported"),
        "Session last renewed · " + Timestamp(SessionRenewedAt, "not reported"),
        "Account label omitted. Add it yourself only if it is needed."
    });

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
