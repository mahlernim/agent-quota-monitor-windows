import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import threading
from .monitor import Monitor, poll_monitor
from .vault import Vault
from .connections import Connections

QUOTA_TYPES = ('codex', 'claude', 'antigravity-gemini', 'antigravity-claude-gpt', 'copilot')


def valid_quota_labels(value):
    """Display initials and names only. They never change identity, grouping, or colors."""
    if not isinstance(value, dict) or not set(value) <= set(QUOTA_TYPES):
        return False
    for label in value.values():
        if not isinstance(label, dict) or set(label) != {'initials', 'name'}:
            return False
        initials, name = label['initials'], label['name']
        if not isinstance(initials, str) or not 1 <= len(initials) <= 3 or not initials.isalnum():
            return False
        if not isinstance(name, str) or not 1 <= len(name) <= 16 or name != name.strip() or not name.isprintable():
            return False
    return True


def app_version(value):
    """The launching app's version, echoed so a newer app can replace an older reader."""
    return value if isinstance(value, str) and re.fullmatch(r'[0-9A-Za-z.+-]{1,64}', value) else 'development'


def handler(monitor, port, connections=None, version='development'):
    expected = f'127.0.0.1:{port}'
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def send(self, status, data, content_type='application/json'):
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Referrer-Policy', 'no-referrer')
            self.send_header('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
            self.end_headers()
            self.wfile.write(data)

        def safe(self, post=False):
            if self.headers.get('Host') != expected:
                return False
            origin = self.headers.get('Origin')
            if origin and origin != 'http://' + expected:
                return False
            if self.headers.get('Sec-Fetch-Site') == 'cross-site':
                return False
            return not post or (origin == 'http://' + expected and self.headers.get('X-Quota-Request') == 'refresh')

        def do_GET(self):
            if not self.safe():
                return self.send(403, b'{}')
            if self.path == '/api/status':
                data = monitor.snapshot()
                data['backend'] = dict(name='agent-quota-monitor', protocolVersion=1, processId=os.getpid(), appVersion=version)
                if connections:
                    data['connections'] = connections.snapshot()
                return self.send(200, json.dumps(data, allow_nan=False).encode())
            if self.path == '/api/desktop':
                with monitor.lock:
                    prefs = monitor.settings_vault.load() if monitor.settings_vault else {}
                keys = ('desktopSelection', 'desktopFloatingSelections', 'desktopFloating', 'desktopOpacity', 'desktopFloatingScale', 'wpfFloatingLeft', 'wpfFloatingTop', 'quotaLabels')
                return self.send(200, json.dumps({key: prefs[key] for key in keys if key in prefs}, allow_nan=False).encode())
            return self.send(404, b'{}')

        def do_POST(self):
            if not self.safe(True):
                return self.send(403, b'{}')
            if self.path == '/api/shutdown':
                if self.headers.get('X-Quota-Process-Id') != str(os.getpid()):
                    return self.send(409, b'{"error":"Quota reader process changed"}')
                self.send(202, b'{"accepted":true}')
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return
            if self.path == '/api/desktop':
                try:
                    size = int(self.headers.get('Content-Length', '0'))
                    if not 0 < size <= 32768 or self.headers.get_content_type() != 'application/json':
                        raise ValueError()
                    self.connection.settimeout(5)
                    data = json.loads(self.rfile.read(size))
                    if not isinstance(data, dict): raise ValueError()
                    for key, value in data.items():
                        if key == 'desktopFloating' and isinstance(value, bool): continue
                        if key == 'quotaLabels' and valid_quota_labels(value): continue
                        if key in ('desktopOpacity', 'desktopFloatingScale', 'wpfFloatingLeft', 'wpfFloatingTop') and type(value) in (int, float):
                            import math
                            if math.isfinite(value) and (35 <= value <= 100 if key == 'desktopOpacity' else 75 <= value <= 200 if key == 'desktopFloatingScale' else -100000 <= value <= 100000): continue
                        values = [value] if key == 'desktopSelection' else value if key == 'desktopFloatingSelections' else None
                        if isinstance(values, list) and len(values) <= 100 and all(isinstance(v, dict) and set(v) == {'accountId', 'groupId', 'bucketId'} and all(isinstance(s, str) and len(s) <= 512 for s in v.values()) for v in values): continue
                        raise ValueError()
                    with monitor.lock:
                        if not monitor.settings_vault: raise ValueError()
                        prefs = monitor.settings_vault.load()
                        prefs.update(data)
                        monitor.settings_vault.save(prefs)
                    return self.send(200, b'{"saved":true}')
                except (ValueError, TypeError, TimeoutError):
                    return self.send(400, b'{}')
                except Exception:
                    return self.send(503, b'{"error":"Could not save desktop preferences"}')
            if self.path in ('/api/accounts/remove', '/api/accounts/restore', '/api/accounts/layout', '/api/accounts/rings', '/api/connections/start', '/api/connections/cancel', '/api/providers'):
                try:
                    size = int(self.headers.get('Content-Length', '0'))
                    if not 0 < size <= 32768 or self.headers.get_content_type() != 'application/json':
                        return self.send(400, b'{}')
                    self.connection.settimeout(5)
                    payload = json.loads(self.rfile.read(size))
                    if not isinstance(payload, dict):
                        return self.send(400, b'{}')
                    if self.path == '/api/providers':
                        monitor.set_providers(payload.get('enabled'))
                        threading.Thread(target=monitor.refresh, daemon=True).start()
                        return self.send(200, b'{"saved":true}')
                    if self.path.startswith('/api/connections/'):
                        if not connections:
                            return self.send(503, b'{}')
                        try:
                            if self.path.endswith('/start'):
                                result = connections.start(payload.get('provider'), payload.get('accountId'),
                                                           payload.get('verify', False))
                            else:
                                connections.cancel(payload.get('jobId'))
                                result = {'cancelled': True}
                            return self.send(202, json.dumps(result).encode())
                        except (ValueError, RuntimeError) as err:
                            return self.send(409 if isinstance(err, RuntimeError) else 400, json.dumps({'error': str(err)}).encode())
                    if self.path.endswith('/layout'):
                        if not monitor.save_layout(payload.get('order'), payload.get('removed')):
                            return self.send(409, b'{"error":"Account list changed. Cancel editing and try again."}')
                    elif self.path.endswith('/rings'):
                        if not monitor.save_ring_order(payload.get('order')):
                            return self.send(409, b'{"error":"Quota rings changed. Refresh and try again."}')
                    elif self.path.endswith('/remove'):
                        account_id = payload.get('accountId')
                        if not isinstance(account_id, str):
                            return self.send(400, b'{}')
                        if not monitor.remove_account(account_id):
                            return self.send(404, b'{}')
                    else:
                        monitor.restore_accounts()
                    return self.send(200, b'{"saved":true}')
                except (ValueError, TimeoutError):
                    return self.send(400, b'{}')
                except Exception:
                    return self.send(503, b'{"error":"Could not save account preferences"}')
            if self.path not in ('/api/refresh', '/api/wake'):
                return self.send(404, b'{}')
            threading.Thread(target=monitor.wake if self.path == '/api/wake' else monitor.refresh, daemon=True).start()
            self.send(202, b'{"accepted":true}')
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--app-version', default='development')
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error('Choose a port between 1024 and 65535')
    settings = Vault(Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard' / 'settings.dpapi')
    try:
        desired = settings.load().get('desiredGoogleAccounts', [])
    except Exception:
        desired = []
    monitor = Monitor(Vault(), desired_google=desired, settings_vault=settings)
    connections = Connections(monitor)
    server = ThreadingHTTPServer(('127.0.0.1', args.port), handler(monitor, args.port, connections, app_version(args.app_version)))
    stop = threading.Event()
    threading.Thread(target=poll_monitor, args=(monitor, stop), daemon=True).start()
    print(f'Quota backend listening on 127.0.0.1:{args.port}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        connections.close()
        server.server_close()


if __name__ == '__main__':
    main()
