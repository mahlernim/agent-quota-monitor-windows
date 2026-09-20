const isRecord = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function normalizeQuotaPools(snapshots) {
  if (!isRecord(snapshots)) return [];
  return Object.entries(snapshots).map(([id, quota]) => {
    if (!isRecord(quota)) return {id};
    return {
      id,
      entitlement: quota.entitlement,
      remaining: quota.quota_remaining,
      remainingPercentage: quota.percent_remaining,
      unlimited: quota.unlimited,
      hasQuota: quota.has_quota,
      tokenBasedBilling: quota.token_based_billing,
      overage: quota.overage_count,
      overageAllowed: quota.overage_permitted,
    };
  });
}
