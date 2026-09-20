"""Copilot SDK lifecycle and locally bound provider identity."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from . import model
from .providers import ReadError, account
from .vault import Vault


def descriptor_vault():
    return Vault(Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard/copilot-account.dpapi')


def command():
    local = Path(os.environ['LOCALAPPDATA'])
    node = shutil.which('node.exe') or shutil.which('node')
    gh = shutil.which('gh.exe') or shutil.which('gh')
    cli = shutil.which('copilot.exe')
    if not cli:
        cli = next((str(p) for p in (local / 'Microsoft/WinGet/Packages').glob('GitHub.Copilot_*/copilot.exe')), None)
    sdk = local / 'QuotaDashboard/copilot-runtime/node_modules/@github/copilot-sdk/dist/index.js'
    if not node or not cli or not gh or not sdk.is_file():
        raise ReadError('copilot_setup_required')
    return [node, str(Path(__file__).with_name('copilot_bridge.mjs')), cli, gh]


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


def copilot_account():
    # Discovery is local only. Hidden accounts never launch a quota reader.
    binding = descriptor_vault().load()
    subject = binding.get('id')
    if not isinstance(subject,int) or isinstance(subject,bool) or subject<=0:
        raise ReadError('copilot_setup_required')
    def read():
        raw = read_snapshot()
        if raw['id'] != subject:
            raise ReadError('identity_changed')
        return model.copilot(raw), raw['login']
    return account('copilot',str(subject),binding.get('login','GitHub account'),
                   'Official Copilot SDK / existing GitHub CLI session',read)


def main():
    raw = read_snapshot()
    groups = model.copilot(raw)
    if not groups:
        raise ReadError('quota_not_reported')
    descriptor_vault().save({'id':raw['id'],'login':raw['login']})
    print(json.dumps({'connected':raw['login'],'groups':groups}))


if __name__ == '__main__':
    main()
