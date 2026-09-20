import test from 'node:test';
import assert from 'node:assert/strict';
import {normalizeQuotaPools} from '../quota/copilot_bridge_data.mjs';

test('null quota pools remain unknown without hiding valid siblings', () => {
  const pools = normalizeQuotaPools({
    chat: {quota_remaining: 90, percent_remaining: 90, unlimited: false},
    premium_interactions: null,
  });

  assert.equal(pools.length, 2);
  assert.deepEqual(pools[0], {
    id: 'chat',
    entitlement: undefined,
    remaining: 90,
    remainingPercentage: 90,
    unlimited: false,
    hasQuota: undefined,
    tokenBasedBilling: undefined,
    overage: undefined,
    overageAllowed: undefined,
  });
  assert.deepEqual(pools[1], {id: 'premium_interactions'});
  assert.equal(Object.hasOwn(pools[1], 'unlimited'), false);
});
