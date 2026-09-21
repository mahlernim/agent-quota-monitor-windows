"""Run the actual .NET request helper against an isolated Python backend."""
from http.server import ThreadingHTTPServer
from pathlib import Path
import subprocess
import sys
import threading

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root))
from quota.monitor import Monitor
from quota.server import handler
from test_removal import Store

monitor = Monitor(Store({'fixture-account': dict(id='fixture-account', provider='claude', label='fixture',
    source='fixture', groups=[], status='pending', lastSuccess=None)}), settings_vault=Store())
server = ThreadingHTTPServer(('127.0.0.1', 0), handler(monitor, 0))
server.RequestHandlerClass = handler(monitor, server.server_port)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    result = subprocess.run([sys.argv[1], 'run', '--project', str(root / 'tests/native-requests/RequestTests.csproj'),
                             '--', f'http://127.0.0.1:{server.server_port}'], timeout=90)
    sys.exit(result.returncode)
finally:
    server.shutdown()
    server.server_close()
    thread.join(3)
