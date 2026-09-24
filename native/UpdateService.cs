using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AgentQuotaMonitor;

internal sealed record ReleaseInfo(string Tag, string Url, bool Installer = false);
internal sealed class UpdatePreferences
{
    public bool Automatic { get; set; } = true;
    public DateTimeOffset LastAttempt { get; set; }
    public string Skipped { get; set; } = "";
    public DateTimeOffset LaterUntil { get; set; }
}
internal sealed class UpdateService : IDisposable
{
    internal const string Repository = "https://github.com/mahlernim/agent-quota-monitor-windows";
    internal static string InstalledVersion => typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    private readonly HttpClient client;
    private readonly HttpClient downloads;
    private readonly Action<UpdatePreferences> save;
    private readonly Func<DateTimeOffset> clock;
    private readonly string version;
    internal UpdatePreferences Preferences { get; }
    internal ReleaseInfo? Available { get; private set; }
    internal string Status { get; private set; } = "";
    internal bool Busy { get; private set; }
    internal event Action? Changed;
    internal UpdateService(HttpClient? client = null, UpdatePreferences? preferences = null, Action<UpdatePreferences>? save = null, Func<DateTimeOffset>? clock = null, string? version = null, HttpClient? downloads = null)
    {
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        // Redirects are followed manually so each hop can be checked against the GitHub hosts.
        this.downloads = downloads ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        this.downloads.DefaultRequestHeaders.UserAgent.ParseAdd("AgentQuotaMonitor/" + InstalledVersion);
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentQuotaMonitor/" + InstalledVersion);
        this.client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        Preferences = preferences ?? Load(); this.save = save ?? Save; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.version = version ?? InstalledVersion;
    }
    private static UpdatePreferences Load() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\AgentQuotaMonitor\Updates"); return JsonSerializer.Deserialize<UpdatePreferences>(key?.GetValue("Preferences") as string ?? "{}") ?? new(); } catch { return new(); } }
    private static void Save(UpdatePreferences prefs) { using var key = Registry.CurrentUser.CreateSubKey(@"Software\AgentQuotaMonitor\Updates"); key.SetValue("Preferences", JsonSerializer.Serialize(prefs)); }
    private void Persist() { try { save(Preferences); } catch { Status = "Could not save update preferences."; } Changed?.Invoke(); }
    internal void SetAutomatic(bool value) { Preferences.Automatic = value; Persist(); }
    internal void Later() { Preferences.LaterUntil = clock().AddDays(1); Available = null; Persist(); }
    internal void Skip() { if (Available is not null) Preferences.Skipped = Available.Tag; Available = null; Persist(); }
    /// <summary>Automatic checks run at most this often, counting failed attempts.</summary>
    internal static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(3);

    /// <summary>An urgent check skips the automatic interval but still requires automatic checks to be enabled.</summary>
    internal async Task Check(bool manual = false, bool urgent = false)
    {
        if (Busy || (!manual && (!Preferences.Automatic || (!urgent && clock() - Preferences.LastAttempt < AutomaticInterval)))) return;
        Busy = true; Preferences.LastAttempt = clock(); Status = "Checking for updates…"; Persist();
        try {
            using var response = await client.GetAsync("https://api.github.com/repos/mahlernim/agent-quota-monitor-windows/releases?per_page=100");
            response.EnsureSuccessStatusCode();
            var release = Select(await response.Content.ReadAsStringAsync(), version);
            Available = release is not null && (manual || (release.Tag != Preferences.Skipped && clock() >= Preferences.LaterUntil)) ? release : null;
            Status = release is null ? "You have the latest version for your release channel." : Available is null ? "Update reminder dismissed." : $"Version {release.Tag.TrimStart('v')} is available.";
        } catch { if (manual) Status = "Could not check for updates. Try again later."; else Status = ""; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    internal static ReleaseInfo? Select(string json, string installed)
    {
        var current = SemVersion.Parse(installed); if (current is null) return null;
        using var data = JsonDocument.Parse(json);
        ReleaseInfo? best = null; SemVersion newest = current;
        foreach (var item in data.RootElement.EnumerateArray()) {
            if (item.GetProperty("draft").GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? ""; var candidate = SemVersion.Parse(tag);
            if (candidate is null || (current.Pre.Length == 0 && (candidate.Pre.Length > 0 || item.GetProperty("prerelease").GetBoolean())) || candidate.CompareTo(newest) <= 0) continue;
            if (!item.TryGetProperty("assets", out var assets) || !assets.EnumerateArray().Any(a => (a.GetProperty("name").GetString() ?? "").EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase) && a.GetProperty("state").GetString() == "uploaded")) continue;
            // Construct the destination from a validated version, never a response-provided URL.
            string installer = InstallerName(tag);
            bool Uploaded(string name) => assets.EnumerateArray().Any(a => a.GetProperty("name").GetString() == name && a.GetProperty("state").GetString() == "uploaded");
            newest = candidate; best = new(tag, Repository + "/releases/tag/" + Uri.EscapeDataString(tag), Uploaded(installer) && Uploaded(installer + ".sha256"));
        }
        return best;
    }

    internal static string InstallerName(string tag) => $"agent-quota-monitor-windows-{tag.TrimStart('v')}-setup-win-x64.exe";
    internal static Uri AssetUri(string tag, string name) =>
        new(Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
    // GitHub serves release files from github.com and redirects to its own download hosts.
    internal static readonly string[] DownloadHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Downloads the release installer and returns its path only when the published SHA-256 checksum matches.</summary>
    internal async Task<string> DownloadInstaller(ReleaseInfo release, string directory, CancellationToken cancellationToken = default)
    {
        if (!release.Installer || SemVersion.Parse(release.Tag) is null) throw new InvalidDataException("This release has no installer.");
        string name = InstallerName(release.Tag);
        string checksum = Encoding.ASCII.GetString(await Fetch(AssetUri(release.Tag, name + ".sha256"), 4096, cancellationToken));
        var match = Regex.Match(checksum.Trim(), @"^([0-9a-fA-F]{64}) [ *]?(\S+)$");
        if (!match.Success || match.Groups[2].Value != name) throw new InvalidDataException("The published checksum is invalid.");
        byte[] installer = await Fetch(AssetUri(release.Tag, name), 512L * 1024 * 1024, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(installer), Convert.FromHexString(match.Groups[1].Value)))
            throw new InvalidDataException("The downloaded installer does not match its published checksum.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, installer, cancellationToken);
        return path;
    }

    private async Task<byte[]> Fetch(Uri uri, long limit, CancellationToken cancellationToken)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The download left GitHub.");
            using var response = await downloads.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = response.Headers.Location is Uri next ? (next.IsAbsoluteUri ? next : new Uri(uri, next)) : throw new InvalidDataException("Redirect without a destination.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The download is too large.");
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("The download is too large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("Too many redirects.");
    }

    public void Dispose() { client.Dispose(); downloads.Dispose(); }
}
internal sealed record SemVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Pre) : IComparable<SemVersion>
{
    internal static SemVersion? Parse(string value) {
        var m = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!m.Success) return null;
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsDigit))) return null;
        return new(BigInteger.Parse(m.Groups[1].Value), BigInteger.Parse(m.Groups[2].Value), BigInteger.Parse(m.Groups[3].Value), pre);
    }
    public int CompareTo(SemVersion? other) {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (Pre.Length == 0 || other.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0).CompareTo(other.Pre.Length == 0 ? 1 : 0);
        for (int i=0; i<Math.Min(Pre.Length, other.Pre.Length); i++) {
            var a = BigInteger.TryParse(Pre[i], out var an); var b = BigInteger.TryParse(other.Pre[i], out var bn);
            result = a && b ? an.CompareTo(bn) : a != b ? (a ? -1 : 1) : string.CompareOrdinal(Pre[i], other.Pre[i]);
            if (result != 0) return result;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
}

