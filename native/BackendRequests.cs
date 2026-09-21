using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgentQuotaMonitor;

internal static class BackendRequests
{
    internal static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object payload)
    {
        // The bounded loopback API requires Content-Length, not chunked JSON.
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync(path, content);
    }
}
