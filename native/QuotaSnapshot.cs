using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AgentQuotaMonitor;

internal static class QuotaSnapshot
{
    internal static List<QuotaItem> Parse(JsonElement root)
    {
        var accounts = Array(root, "accounts");
        var accountIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement account in accounts.EnumerateArray())
        {
            UniqueId(account, accountIds);
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement group in Array(account, "groups").EnumerateArray())
            {
                UniqueId(group, groupIds);
                var bucketIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement bucket in Array(group, "buckets").EnumerateArray())
                    UniqueId(bucket, bucketIds);
            }
        }
        List<QuotaItem> items = QuotaItem.Parse(root);
        if (!root.TryGetProperty("ringOrder", out JsonElement saved) || saved.ValueKind != JsonValueKind.Array)
            return items;
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int rank = 0;
        foreach (JsonElement selection in saved.EnumerateArray())
        {
            if (selection.ValueKind != JsonValueKind.Object) continue;
            string key = QuotaItem.MakeKey(QuotaItem.Text(selection, "accountId"),
                QuotaItem.Text(selection, "groupId"), QuotaItem.Text(selection, "bucketId"));
            order.TryAdd(key, rank++);
        }
        // Account order comes from the backend. Move only rings within each account.
        return items.GroupBy(item => item.AccountId)
            .SelectMany(group => group.Select((item, index) => (item, index))
                .OrderBy(pair => order.GetValueOrDefault(pair.item.Key, int.MaxValue))
                .ThenBy(pair => pair.index).Select(pair => pair.item)).ToList();
    }

    private static JsonElement Array(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new JsonException("The quota snapshot is incomplete.");
        return value;
    }

    private static void UniqueId(JsonElement value, HashSet<string> ids)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!))
            throw new JsonException("The quota snapshot has missing or duplicate identities.");
    }
}
