"""Provider-specific targets, preserving the v0.4 Claude five-hour selection key."""
from . import claude_cli

AG_SOURCE = 'Official Antigravity CLI /usage (desktop app not required)'
CODEX_SOURCE = 'Official Codex session / usage endpoint'


def targets(row):
    provider = row.get('provider')
    if provider == 'claude':
        specs = [('direct', 'five_hour', 18000, 'Claude · five-hour'),
                 ('direct', 'seven_day', 604800, 'Claude · weekly')]
    elif provider == 'antigravity':
        specs = [(group, window, seconds, 'Antigravity ' + label + ' · ' + duration)
                 for group, label in [('gemini', 'Gemini'), ('claude-gpt', 'Claude/GPT')]
                 for window, seconds, duration in [('5h', 18000, 'five-hour'), ('weekly', 604800, 'weekly')]]
    elif provider == 'codex':
        # No synthetic five-hour limit for plans that do not report one.
        specs = [('codex', 'weekly', 604800, 'Codex · weekly')]
    else:
        return []
    return [dict(row, activationKey=row['id'] if provider == 'claude' and window == 'five_hour'
                 else '|'.join((row['id'], group, window)), activationGroup=group,
                 activationWindow=window, activationSeconds=seconds, activationLabel=label)
            for group, window, seconds, label in specs]


def target(row):
    return row if 'activationKey' in row else targets(row)[0]


def classify(row, now):
    from .activation import MAX_GAP, number, timestamp
    if row.get('provider') not in ('claude', 'codex', 'antigravity'):
        return 'unsupported', None
    row = target(row)
    provider = row['provider']
    sources = {'claude': ('Official Claude Code session / OAuth usage endpoint', claude_cli.SOURCE),
               'antigravity': (AG_SOURCE,), 'codex': (CODEX_SOURCE,)}
    if row.get('source') not in sources[provider]:
        return 'unsupported', None
    read = row.get('lastSuccess')
    if row.get('status') != 'live' or row.get('error') or not number(read) or not 0 <= now-read <= MAX_GAP:
        return 'stale', None
    groups = [g for g in row.get('groups', []) if g.get('id') == row['activationGroup']]
    if len(groups) != 1:
        return 'unknown', None
    group = groups[0]
    buckets = group.get('buckets', [])
    if len({b.get('id') for b in buckets}) != len(buckets):
        return 'unknown', None
    if provider == 'codex':
        chosen = [b for b in buckets if b.get('windowSeconds') == 604800]
        weekly = chosen
        relevant = buckets
    else:
        chosen = [b for b in buckets if b.get('id') == row['activationWindow']]
        weekly = [b for b in buckets if b.get('id') == ('seven_day' if provider == 'claude' else 'weekly')]
        relevant = [b for b in buckets if b.get('id') in
                    (('five_hour', 'seven_day') if provider == 'claude' else ('5h', 'weekly'))]
        expected = {'five_hour': 18000, 'seven_day': 604800, '5h': 18000, 'weekly': 604800}
        if len(relevant) != 2 or any(b.get('windowSeconds') != expected[b['id']] for b in relevant):
            return 'unavailable', None
    if len(chosen) != 1 or len(weekly) != 1:
        return 'unknown', None
    if any(b.get('disabled') is True or b.get('available') is False or
           not number(b.get('remaining')) or not 0 < b['remaining'] <= 100 for b in relevant):
        return 'unavailable', None
    bucket = chosen[0]
    if bucket.get('windowSeconds') != row['activationSeconds']:
        return 'unknown', None
    reset = timestamp(bucket.get('resetsAt'))
    if reset is not None and reset > now:
        # This is a candidate, never inactivity from a single full-quota reading.
        # The observer must distinguish a moving deadline from a fixed deadline.
        if provider != 'claude' and bucket['remaining'] == 100 and abs(reset-read-row['activationSeconds']) <= 60:
            return 'full_deadline', reset
        return 'running', reset
    if provider == 'claude' and bucket.get('resetsAt') is None and bucket['remaining'] == 100 and bucket.get('inactiveReported') is True:
        if group.get('extraUsageEnabled') is not False:
            return 'billing_unknown', None
        return 'inactive', None
    return 'unknown', None
