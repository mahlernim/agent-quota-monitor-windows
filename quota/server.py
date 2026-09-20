import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import sys
import threading
from .monitor import Monitor
from .vault import Vault
from .connections import Connections

def bundled_path(*parts):
    """Resolve read-only assets in source, one-folder, and one-file builds."""
    root = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parents[1]))
    return root.joinpath(*parts)


WEB = bundled_path('web')


def handler(monitor, port, connections=None):
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
                if connections:
                    data['connections'] = connections.snapshot()
                return self.send(200, json.dumps(data, allow_nan=False).encode())
            if self.path == '/api/desktop':
                with monitor.lock:
                    prefs = monitor.settings_vault.load() if monitor.settings_vault else {}
                keys = ('desktopSelection', 'desktopFloatingSelections', 'desktopFloating', 'desktopOpacity', 'desktopFloatingScale', 'wpfFloatingLeft', 'wpfFloatingTop')
                return self.send(200, json.dumps({key: prefs[key] for key in keys if key in prefs}, allow_nan=False).encode())
            files = {'/': ('index.html', 'text/html; charset=utf-8'), '/app.js': ('app.js', 'text/javascript; charset=utf-8'), '/style.css': ('style.css', 'text/css; charset=utf-8'), '/icon.svg': ('icon.svg', 'image/svg+xml')}
            if self.path not in files:
                return self.send(404, b'{}')
            path, mime = files[self.path]
            self.send(200, (WEB/path).read_bytes(), mime)

        def do_POST(self):
            if not self.safe(True):
                return self.send(403, b'{}')
            if self.path == '/api/shutdown':
                self.send(202, b'{"accepted":true}')
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return
            if self.path == '/api/desktop':
                try:
                    size = int(self.headers.get('Content-Length', '0'))
                    if not 0 < size <= 32768 or self.headers.get('Content-Type') != 'application/json':
                        raise ValueError()
                    self.connection.settimeout(5)
                    data = json.loads(self.rfile.read(size))
                    if not isinstance(data, dict): raise ValueError()
                    for key, value in data.items():
                        if key == 'desktopFloating' and isinstance(value, bool): continue
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
            if self.path in ('/api/accounts/remove', '/api/accounts/restore', '/api/accounts/layout', '/api/connections/start', '/api/connections/cancel', '/api/providers'):
                try:
                    size = int(self.headers.get('Content-Length', '0'))
                    if not 0 < size <= 32768 or self.headers.get('Content-Type') != 'application/json':
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
                                result = connections.start(payload.get('provider'), payload.get('accountId'))
                            else:
                                connections.cancel(payload.get('jobId'))
                                result = {'cancelled': True}
                            return self.send(202, json.dumps(result).encode())
                        except (ValueError, RuntimeError) as err:
                            return self.send(409 if isinstance(err, RuntimeError) else 400, json.dumps({'error': str(err)}).encode())
                    if self.path.endswith('/layout'):
                        if not monitor.save_layout(payload.get('order'), payload.get('removed')):
                            return self.send(409, b'{"error":"Account list changed. Cancel editing and try again."}')
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
            if self.path != '/api/refresh':
                return self.send(404, b'{}')
            threading.Thread(target=monitor.refresh, daemon=True).start()
            self.send(202, b'{"accepted":true}')
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=8765)
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
    server = ThreadingHTTPServer(('127.0.0.1', args.port), handler(monitor, args.port, connections))
    stop = threading.Event()
    def poll():
        while not stop.is_set():
            monitor.refresh()
            stop.wait(60)
    threading.Thread(target=poll, daemon=True).start()
    print(f'Quota Dashboard running at http://127.0.0.1:{args.port}', flush=True)
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
