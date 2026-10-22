"""Copilot SDK lifecycle and locally bound provider identity."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from . import model
from .providers import ReadError, account
from .vault import Vault

NODE_MINIMUM_VERSIONS = {22: (22, 23, 4), 24: (24, 22, 1), 26: (26, 12, 1)}
NODE_UPDATE_HELP = ('Copilot needs a security-updated Node.js runtime. Install Node.js 24.22.1 or newer 24.x, '
                    'or use Node.js 22.23.4 or newer 22.x or 26.12.1 or newer 26.x, then retry.')


def validate_node(node):
    if not node:
        raise ReadError('copilot_node_update_required')
    try:
        result = subprocess.run([node, '--version'], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, text=True, timeout=5, check=True,
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        match = re.fullmatch(r'v(\d+)\.(\d+)\.(\d+)', result.stdout.strip())
        version = tuple(map(int, match.groups())) if match else ()
        minimum = NODE_MINIMUM_VERSIONS.get(version[0]) if version else None
        if minimum is not None and version >= minimum:
            return
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    raise ReadError('copilot_node_update_required')


def descriptor_vault():
    return Vault(Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard/copilot-account.dpapi')


def bundled_path(*parts):
    """Resolve the non-secret bridge shipped beside a frozen application."""
    root = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parents[1]))
    return root.joinpath(*parts)


def command():
    from .providers import refresh_path
    refresh_path()
    local = Path(os.environ['LOCALAPPDATA'])
    node = shutil.which('node.exe') or shutil.which('node')
    gh = shutil.which('gh.exe') or shutil.which('gh')
    cli = shutil.which('copilot.exe')
    if not cli:
        cli = next((str(p) for p in (local / 'Microsoft/WinGet/Packages').glob('GitHub.Copilot_*/copilot.exe')), None)
    sdk = local / 'QuotaDashboard/copilot-runtime/node_modules/@github/copilot-sdk/dist/index.js'
    bridge = bundled_path('quota', 'copilot_bridge.mjs')
    bridge_data = bundled_path('quota', 'copilot_bridge_data.mjs')
    if not node or not cli or not gh or not sdk.is_file() or not bridge.is_file() or not bridge_data.is_file():
        raise ReadError('copilot_setup_required')
    validate_node(node)
    return [node, str(bridge), cli, gh]


def stop_reader(process):
    """Bound cleanup to this bridge and its owned SDK child."""
    try:
        if os.name == 'nt':
            subprocess.run(['taskkill.exe','/PID',str(process.pid),'/T','/F'], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),
                           timeout=10, check=True)
        else:
            process.kill()
        process.wait(timeout=5)
        return
    except (OSError,subprocess.SubprocessError):
        pass
    # A missing/failed taskkill must not leak exception text into the monitor.
    # Retry the owned tree while its parent still exists before the direct fallback.
    if os.name == 'nt' and process.poll() is None:
        try:
            subprocess.run(['taskkill.exe','/PID',str(process.pid),'/T','/F'], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),
                           timeout=5, check=True)
        except (OSError,subprocess.SubprocessError):
            pass
    try:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
    except (OSError,subprocess.SubprocessError):
        raise ReadError('copilot_cleanup_failed') from None


def read_snapshot():
    try:
        process = subprocess.Popen(command(), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            stdout, _ = process.communicate(timeout=45)
        except subprocess.TimeoutExpired:
            stop_reader(process)
            raise ReadError('copilot_read_timeout') from None
        if len(stdout)>2*1024*1024:
            raise ReadError('response_too_large')
        raw = json.loads(stdout)
        if not isinstance(raw, dict):
            raise ReadError('schema_changed')
        if raw.get('error'):
            raise ReadError(raw['error'], raw.get('retryAfter', 0))
        if process.returncode or not isinstance(raw.get('id'),int) or isinstance(raw['id'],bool) or raw['id']<=0 or not isinstance(raw.get('login'),str):
            raise ReadError('copilot_reader_failed')
        return raw
    except (OSError,ValueError):
        raise ReadError('copilot_reader_failed') from None


def setup_ready():
    """True when the official tools and supported runtime are ready. It never starts a reader."""
    try:
        command()
        return True
    except ReadError:
        return False


def copilot_account():
    # Discovery is local only. Hidden accounts never launch a quota reader.
    binding = descriptor_vault().load()
    subject = binding.get('id')
    if not isinstance(subject,int) or isinstance(subject,bool) or subject<=0:
        # Setup can be complete while no GitHub account has been linked yet.
        command()
        raise ReadError('copilot_not_connected')
    def read():
        raw = read_snapshot()
        if raw['id'] != subject:
            raise ReadError('identity_changed')
        return model.copilot(raw), raw['login']
    return account('copilot',str(subject),binding.get('login','GitHub account'),
                   'Official Copilot SDK / existing GitHub CLI session',read)


def bind_current_account(stop=None):
    raw = read_snapshot()
    groups = model.copilot(raw)
    if not groups:
        raise ReadError('quota_not_reported')
    if stop is not None and stop.is_set():
        raise ReadError('connection_cancelled')
    descriptor_vault().save({'id':raw['id'],'login':raw['login']})
    return {'connected':raw['login'],'groups':groups}


def main():
    print(json.dumps(bind_current_account()))


if __name__ == '__main__':
    main()
