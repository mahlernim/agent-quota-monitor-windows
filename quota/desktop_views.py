"""Pure presentation helpers for the Windows desktop shell."""
from datetime import datetime, timezone
import math


def _windows(snapshot):
    for account in snapshot.get('accounts', []):
        for group in account.get('groups', []):
            for bucket in group.get('buckets', []):
                yield account, group, bucket


def selected_window(snapshot, selected=None):
    """Return one safe-to-display quota window and its stable selection key."""
    rows = list(_windows(snapshot))
    if not rows:
        return None, None
    if isinstance(selected, dict):
        wanted = (selected.get('accountId'), selected.get('groupId'), selected.get('bucketId'))
        for account, group, bucket in rows:
            if wanted == (account.get('id'), group.get('id'), bucket.get('id')):
                return (account, group, bucket), dict(accountId=wanted[0], groupId=wanted[1], bucketId=wanted[2])
        # A removed or temporarily unavailable selected account must never be
        # replaced silently by another account in the tray.
        return None, selected
    account, group, bucket = next((row for row in rows if row[2].get('remaining') is not None), rows[0])
    return (account, group, bucket), dict(accountId=account.get('id'), groupId=group.get('id'), bucketId=bucket.get('id'))


def display_remaining(bucket):
    value = bucket.get('remaining')
    if bucket.get('unlimited') is True:
        return 'Unlimited', None
    if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value):
        return f'{max(0, min(100, value)):.0f}%', max(0, min(100, value))
    return 'Unknown', None


def exact_remaining(bucket):
    """Keep provider precision for native tooltip text without inventing it."""
    value = bucket.get('remaining')
    if bucket.get('unlimited') is True:
        return 'Unlimited'
    if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and 0 <= value <= 100:
        return f'{value:.1f}'.rstrip('0').rstrip('.') + '%'
    return 'Unknown'


def reset_label(value, now=None):
    if not value:
        return 'Reset unknown'
    try:
        target = datetime.fromisoformat(value.replace('Z', '+00:00'))
        now = now or datetime.now(timezone.utc)
        seconds = int((target - now).total_seconds())
        if seconds <= 0:
            return 'Reset time passed'
        hours, remainder = divmod(seconds, 3600)
        minutes = remainder // 60
        return f'Resets in {hours}h {minutes:02d}m'
    except (TypeError, ValueError):
        return 'Reset unknown'


def compact_group(label):
    return {'Gemini Models': 'Gemini', 'Claude and GPT models': 'Claude-GPT',
            'Direct Anthropic subscription': 'Anthropic'}.get(label, label)


def compact_window(bucket):
    seconds = bucket.get('windowSeconds')
    if seconds == 18000:
        return '5h'
    if seconds == 604800:
        return '7d'
    if bucket.get('windowKind') == 'monthly':
        return 'Month'
    return bucket.get('label', 'Window')


def last_success_label(value):
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return 'Never'
    try:
        return datetime.fromtimestamp(value, timezone.utc).strftime('%Y-%m-%d %H:%M UTC')
    except (OverflowError, OSError, ValueError):
        return 'Unknown'


def tray_code(account, group):
    provider = account.get('provider')
    if provider == 'codex':
        return 'CX'
    if provider == 'claude':
        return 'CL'
    if provider == 'copilot':
        return 'CP'
    if provider == 'antigravity':
        label = group.get('label', '')
        if label == 'Gemini Models':
            return 'GM'
        if label == 'Claude and GPT models':
            return 'CG'
    return '?'


def quota_pace(bucket, live=True, now=None):
    """Compare quota remaining and time remaining only for known fixed windows."""
    duration = bucket.get('windowSeconds')
    if duration not in (18000, 604800):
        return None, 'Time window not reported'
    value = bucket.get('remaining')
    if not live or bucket.get('available') is False:
        return None, 'Pace unavailable until quota is live'
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or not 0 <= value <= 100:
        return None, 'Pace unavailable without quota remaining'
    try:
        reset = datetime.fromisoformat(bucket['resetsAt'].replace('Z', '+00:00'))
        seconds = (reset - (now or datetime.now(timezone.utc))).total_seconds()
        if not 0 < seconds <= duration:
            return None, 'Reset time is outside the current window'
    except (KeyError, AttributeError, TypeError, ValueError):
        return None, 'Reset time not reported'
    remaining = seconds / duration * 100
    difference = value - remaining
    return remaining, f'{remaining:.1f}% time left | {"Within pace" if difference >= 0 else "Faster usage"}'


def popup_rows(snapshot):
    """Flatten live and stale windows without inventing quota values."""
    rows = []
    for account, group, bucket in _windows(snapshot):
        remaining, numeric = display_remaining(bucket)
        time_remaining, pace = quota_pace(bucket, account.get('status') == 'live')
        rows.append(dict(accountId=account.get('id'), groupId=group.get('id'), bucketId=bucket.get('id'),
                         provider=account.get('provider', 'Unknown'), account=account.get('label', 'Unreported account'),
                         group=compact_group(group.get('label', 'Unreported group')), window=compact_window(bucket),
                         remaining=remaining, exact=exact_remaining(bucket), numeric=numeric, status=account.get('status', 'pending'),
                         code=tray_code(account, group), timeRemaining=time_remaining, pace=pace,
                         reset=reset_label(bucket.get('resetsAt')).replace('Resets in ', ''),
                         lastSuccess=last_success_label(account.get('lastSuccess'))))
    return rows
