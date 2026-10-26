import hashlib
import math
from datetime import datetime, timezone
from .retry import MAX_TIMESTAMP, finite_number


def identity(provider, subject):
    return provider + '-' + hashlib.sha256(subject.encode()).hexdigest()[:24]


def percent(value, scale=1):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    value *= scale
    return value if 0 <= value <= 100 and math.isfinite(value) else None


def timestamp(value):
    try:
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            return datetime.fromtimestamp(value, timezone.utc).isoformat()
        if isinstance(value, str):
            parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
            if parsed.tzinfo:
                return parsed.astimezone(timezone.utc).isoformat()
    except (ValueError, OverflowError, OSError):
        pass
    return None


def bucket(key, label, remaining, seconds=None, reset=None):
    return dict(id=key, label=label, remaining=remaining, windowSeconds=seconds,
                resetsAt=timestamp(reset))


def codex(raw):
    """Accept additive fields, but reject ambiguous identities and changed known shapes."""
    if not isinstance(raw, dict):
        raise ValueError('Invalid Codex usage object')
    groups = []
    rates = [('codex', 'Codex', raw.get('rate_limit'))]
    if raw.get('code_review_rate_limit') is not None:
        rates.append(('review', 'Code review', raw['code_review_rate_limit']))
    additional = raw.get('additional_rate_limits')
    if additional is None:
        additional = []
    if not isinstance(additional, list):
        raise ValueError('Invalid Codex additional limits')
    for item in additional:
        if not isinstance(item, dict):
            raise ValueError('Invalid Codex additional limit')
        key = item.get('limit_name')
        if not isinstance(key, str) or not key.strip():
            raise ValueError('Missing Codex limit identity')
        rates.append((key, key, item.get('rate_limit')))
    seen = set()
    for key, name, rate in rates:
        if key in seen:
            raise ValueError('Duplicate Codex limit identity')
        seen.add(key)
        if rate is None:
            continue
        if not isinstance(rate, dict):
            raise ValueError('Invalid Codex rate limit')
        buckets = []
        for field in ('primary_window', 'secondary_window'):
            w = rate.get(field)
            if w is None:
                continue
            if not isinstance(w, dict):
                raise ValueError('Invalid Codex window')
            used = percent(w.get('used_percent'))
            seconds = w.get('limit_window_seconds')
            if not finite_number(seconds) or not 0 < seconds <= MAX_TIMESTAMP:
                seconds = None
            label = {18000: 'Five-hour window', 604800: 'Weekly window'}.get(seconds, 'Reported window')
            buckets.append(bucket(key + '/' + field, label, None if used is None else 100-used, seconds, w.get('reset_at')))
        groups.append(dict(id=key, label=name, buckets=buckets))
    return groups


def claude(raw):
    buckets = []
    for name, w in raw.items():
        if not (name.startswith('five_hour') or name.startswith('seven_day')) or name.endswith('_breakdown') or not isinstance(w, dict):
            continue
        used = percent(w.get('utilization'))
        buckets.append(bucket(name, name.replace('_', ' ').title(), None if used is None else 100-used,
                              18000 if name.startswith('five_hour') else 604800, w.get('resets_at')))
        # A missing or malformed reset must not become evidence of inactivity.
        buckets[-1]['inactiveReported'] = used == 0 and 'resets_at' in w and w['resets_at'] is None
    extra = raw.get('extra_usage')
    enabled = extra.get('is_enabled') if isinstance(extra, dict) else None
    return [dict(id='direct', label='Direct Anthropic subscription', buckets=buckets,
                 extraUsageEnabled=enabled if isinstance(enabled, bool) else None)] if buckets else []


def antigravity(raw):
    root = raw.get('response', raw)
    groups = []
    for i, group in enumerate(root.get('groups') or []):
        name = group.get('displayName', 'Unreported group')
        key = str(group.get('id', i))
        buckets = []
        for j, w in enumerate(group.get('buckets') or []):
            seconds = {'5h': 18000, 'weekly': 604800}.get(w.get('window'))
            buckets.append(bucket(str(w.get('bucketId', j)), w.get('displayName', 'Reported window'),
                                  percent(w.get('remainingFraction'), 100), seconds, w.get('resetTime')))
        groups.append(dict(id=key, label=name, buckets=buckets))
    return groups


def copilot(raw):
    """Keep calendar months, numeric allowances, and current availability distinct."""
    groups = []
    sku = raw.get('sku')
    plan = 'Copilot Free' if sku == 'free_limited_copilot' else (sku or raw.get('plan') or 'Copilot plan unreported')
    def amount(value):
        return value if isinstance(value,(int,float)) and not isinstance(value,bool) and math.isfinite(value) and value>=0 else None
    for q in raw.get('pools') or []:
        if not isinstance(q,dict) or not isinstance(q.get('id'),str):
            continue
        key = q['id']
        credit = q.get('tokenBasedBilling') is True and key != 'completions'
        label = {'chat':'Included AI credits' if credit else 'Chat requests', 'completions':'Inline suggestions',
                 'premium_interactions':'Premium AI credits' if credit else 'Premium requests'}.get(key,key.replace('_',' ').title())
        unlimited = q.get('unlimited') is True
        b = bucket(key,label,None if unlimited else percent(q.get('remainingPercentage')),reset=raw.get('reset'))
        b.update(windowKind='monthly',unlimited=unlimited,
                 available=q.get('hasQuota') if isinstance(q.get('hasQuota'),bool) else None,
                 amountRemaining=None if unlimited else amount(q.get('remaining')),
                 entitlement=amount(q.get('entitlement')),unit='credits' if credit else 'suggestions' if key=='completions' else 'requests',
                 overage=amount(q.get('overage')),overageAllowed=q.get('overageAllowed') is True)
        groups.append(dict(id=key,label=label,buckets=[b],plan=plan,
                           models=[m['id'] for m in raw.get('models',[]) if isinstance(m,dict) and isinstance(m.get('id'),str)]))
    return groups
