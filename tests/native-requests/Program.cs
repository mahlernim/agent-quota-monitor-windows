using AgentQuotaMonitor;
using System.Text.Json;

using var http = new HttpClient { BaseAddress = new Uri(args[0]), Timeout = TimeSpan.FromSeconds(5) };
http.DefaultRequestHeaders.Add("Origin", args[0]);
http.DefaultRequestHeaders.Add("X-Quota-Request", "refresh");
using var removed = await BackendRequests.PostAsync(http, "/api/accounts/remove", new { accountId = "fixture-account" });
removed.EnsureSuccessStatusCode();
using var status = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
if (status.RootElement.GetProperty("accounts").GetArrayLength() != 0) throw new Exception("Account was not removed");
using var restored = await BackendRequests.PostAsync(http, "/api/accounts/restore", new {});
restored.EnsureSuccessStatusCode();
using var restoredStatus = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
if (restoredStatus.RootElement.GetProperty("accounts").GetArrayLength() != 1) throw new Exception("Account was not restored");
using var saved = await BackendRequests.PostAsync(http, "/api/desktop", new { desktopOpacity = 70 });
saved.EnsureSuccessStatusCode();
using var prefs = JsonDocument.Parse(await http.GetStringAsync("/api/desktop"));
if (prefs.RootElement.GetProperty("desktopOpacity").GetInt32() != 70) throw new Exception("Settings were not saved");
Console.WriteLine("Native HTTP removal, restore, and settings checks passed");
