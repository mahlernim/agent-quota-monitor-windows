"""Closed diagnostic vocabulary. Never expose provider output or exception text."""
import subprocess

MESSAGES = {
    'identity': 'Subscription identity verification failed.',
    'client': 'The official CLI could not be found or started.',
    'version': 'Antigravity CLI 1.3.6 or newer is required. Update CLI in Settings.',
    'workspace': 'The activation workspace is unavailable or contains unexpected files.',
    'profile': 'The isolated activation profile is unavailable or its configuration changed.',
    'plugins': 'The isolated activation profile still loads plugins.',
    'mcp': 'The isolated activation profile still loads MCP servers.',
    'hooks': 'The isolated activation profile still loads hooks.',
    'billing': 'The isolated activation profile could not verify that credit overages are off.',
    'agent': 'The isolated text-only activation agent could not be verified.',
    'probe': 'The CLI returned an unsupported activation preflight response.',
    'timeout': 'The activation CLI operation timed out.',
    'delivery': 'The CLI did not confirm successful prompt delivery.',
    'tools': 'The CLI reported tool use during text-only activation.',
    'preflight': 'Activation preflight failed.',
    'network': 'The provider could not be reached during activation verification.',
    'sign_in': 'The provider rejected the saved subscription session. Open the official client to renew it.',
    'rate_limited': 'The provider rate-limited activation verification.',
    'certificate': 'Provider certificate verification failed.',
}


class ActivationFailure(ValueError):
    def __init__(self, code):
        self.code = code if code in MESSAGES else 'preflight'
        super().__init__(MESSAGES[self.code])


def error_code(error, default='preflight'):
    if isinstance(error, ActivationFailure):
        return error.code
    if isinstance(error, (TimeoutError, subprocess.TimeoutExpired)):
        return 'timeout'
    code = getattr(error, 'code', None)
    if code in ('claude_cli_timeout', 'antigravity_cli_timeout'):
        return 'timeout'
    if code in ('sign_in_required', 'session_expired', 'local_session_unavailable'):
        return 'sign_in'
    if code in ('network_error', 'network_unreachable', 'provider_unavailable', 'network_unavailable', 'connection_or_response_error'):
        return 'network'
    if code == 'rate_limited':
        return 'rate_limited'
    if code == 'certificate_verification_failed':
        return 'certificate'
    known = {'Activation workspace is not empty': 'workspace', 'Codex runner unavailable': 'client',
             'Runner unavailable': 'client', 'Unverified identity': 'identity',
             'Subscription identity mismatch': 'identity', 'Codex subscription identity mismatch': 'identity'}
    if isinstance(error, ValueError) and str(error) in known:
        return known[str(error)]
    return default


def message(code):
    return MESSAGES.get(code, MESSAGES['preflight'])
