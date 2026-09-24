"""Run the actual WPF startup and owned shutdown against a real isolated backend."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dotnet', help='Path to the .NET 10 SDK executable, or dotnet on PATH')
    parser.add_argument('--backend', type=Path, help='Optional exact packaged quota-backend.exe to exercise')
    parser.add_argument('--report', type=Path, help='Optional copy of the final JSON result')
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('This integration test requires Windows WPF and DPAPI')
    root = Path(__file__).resolve().parents[1]
    sys.path.insert(0, str(root))
    from quota.vault import Vault
    backend = args.backend.resolve(strict=True) if args.backend else None
    if backend and (not backend.is_file() or backend.name.lower() != 'quota-backend.exe'):
        parser.error('--backend must identify the packaged quota-backend.exe')

    evidence = root / 'work' / 'native-startup'
    evidence.mkdir(parents=True, exist_ok=True)
    # Keep new task-owned evidence. Never reuse or clean an existing profile.
    profile = Path(tempfile.mkdtemp(prefix='synthetic-', dir=evidence)).resolve()
    for name in ('local', 'roaming', 'tmp'):
        (profile / name).mkdir()
    vault = Vault(profile / 'local' / 'QuotaDashboard' / 'settings.dpapi')
    preferences = {'enabledProviders': [], 'desktopFloatingSelections': [], 'desktopFloating': True,
                   'desktopOpacity': 70, 'desktopFloatingScale': 125, 'wpfFloatingLeft': -32000, 'wpfFloatingTop': -32000}
    vault.save(preferences)
    initial_hash = hashlib.sha256(vault.path.read_bytes()).hexdigest()
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        port = reservation.getsockname()[1]
    if port == 8765:
        raise RuntimeError('The test must not use the installed application port')
    native_report = profile / 'native-result.json'
    configuration = profile / 'configuration.json'
    configuration.write_text(json.dumps({'Profile': str(profile), 'SourceRoot': str(root),
                                         'Python': sys.executable, 'Backend': str(backend) if backend else None,
                                         'Port': port, 'Report': str(native_report)}, indent=2), encoding='utf-8')
    completed = subprocess.run([args.dotnet, 'run', '--project', str(root / 'tests/native-startup/StartupTests.csproj'),
                                '-c', 'Release', '--', str(configuration)], cwd=root, timeout=120,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    report = json.loads(native_report.read_text(encoding='utf-8')) if native_report.is_file() else {'passed': False}
    final_hash = hashlib.sha256(vault.path.read_bytes()).hexdigest()
    report.update(profile=str(profile), backend=str(backend) if backend else 'source Python backend',
                  native_exit_code=completed.returncode, settings_unchanged=initial_hash == final_hash,
                  settings_match=vault.load() == preferences, settings_sha256=final_hash,
                  provider_configuration='All providers disabled in fresh synthetic profile')
    report['passed'] = bool(report.get('passed') and completed.returncode == 0 and
                            report['settings_unchanged'] and report['settings_match'] and
                            report.get('childExited') and report.get('childExitCode') == 0)
    if report['passed']:
        # An exited child should also have released the listening socket.
        with socket.socket() as check:
            check.settimeout(1)
            report['port_released'] = check.connect_ex(('127.0.0.1', port)) != 0
        report['passed'] = report['port_released']
    final_report = profile / 'result.json'
    final_report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    if args.report:
        args.report.resolve().write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
