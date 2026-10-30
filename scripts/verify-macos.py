#!/usr/bin/env python3
"""Check a built Mac app with disposable synthetic history; never use the operator's workspace or accounts."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import queue
import subprocess
import sys
import tempfile
import threading
import time

FIXTURES = Path(__file__).resolve().parent.parent / 'fixtures'
SAMPLE_PROJECTS = [('atlas', 'Atlas API'), ('studio', 'Studio'), ('orbit', 'Orbit mobile'), ('dispatch', 'Dispatch'), ('platform', 'Platform')]


def write_synthetic_history(root):
    """Writes the sample conversations as ordinary Claude Code session files under root; returns the message count."""
    rows = [json.loads(line) for line in (FIXTURES / 'sample-history.jsonl').read_text(encoding='utf-8').splitlines() if line.strip()]
    shift = time.time() - 30 * 60 - datetime(2026, 10, 16, 10, tzinfo=timezone.utc).timestamp()
    for row in rows:
        cwd = root / 'projects' / row['project']
        cwd.mkdir(parents=True, exist_ok=True)
        at = datetime.fromtimestamp(datetime.fromisoformat(row['at'].replace('Z', '+00:00')).timestamp() + shift, timezone.utc)
        message = ({'role': 'user', 'content': row['text']} if row['author'] == 'user' else
                   {'role': 'assistant', 'id': 'msg-' + row['id'], 'stop_reason': 'end_turn', 'content': [{'type': 'text', 'text': row['text']}]})
        line = {'type': row['author'], 'uuid': row['id'], 'sessionId': row['session'], 'cwd': str(cwd), 'gitBranch': row['branch'],
                'timestamp': at.isoformat().replace('+00:00', 'Z'), 'message': message}
        session = root / 'sessions' / row['project'] / (row['session'] + '.jsonl')
        session.parent.mkdir(parents=True, exist_ok=True)
        with session.open('a', encoding='utf-8') as file: file.write(json.dumps(line) + '\n')
    return len(rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--distribution', action='store_true', help='Also require Gatekeeper acceptance and a stapled notarization ticket')
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('This verification must execute on macOS; a Windows run cannot verify Mac support.')
    app = args.app.resolve(strict=True)
    binaries = app / 'Contents/MacOS'
    integrations = app / 'Contents/Resources/integrations'
    report = {'platform': platform.platform(), 'architecture': platform.machine(), 'passed': False,
              'realHistoryRead': False, 'accountToolsCalled': False, 'uiLifecycleVerified': False,
              'distributionChecksRequested': args.distribution}
    process = None
    try:
        if (binaries / 'mission-control-providers').exists():
            raise RuntimeError('Mac bundle contains the Windows-only GitHub helper; rebuild with scripts/build-tauri.mjs.')
        for name in ['mission-control', 'mission-control-backend', 'mission-control-mcp']:
            binary = binaries / name
            if not binary.is_file() or not os.access(binary, os.X_OK):
                raise RuntimeError('Missing or non-executable bundled component: ' + name)
        for file in ['setup.py', 'manage.py', 'verify_memory_package.py', 'shared/recap-reader.md', 'shared/recap-coordinator.md']:
            if not (integrations / file).is_file(): raise RuntimeError('Missing setup resource: ' + file)
        subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
        report['codeSignatureVerified'] = True
        if args.distribution:
            subprocess.run(['spctl', '--assess', '--type', 'execute', str(app)], check=True)
            subprocess.run(['xcrun', 'stapler', 'validate', str(app)], check=True)
            report['gatekeeperAndNotarizationVerified'] = True
        with tempfile.TemporaryDirectory(prefix='mission-control-mac-smoke-', dir='/private/tmp') as temporary:
            data = Path(temporary).resolve()
            process = subprocess.Popen([str(binaries / 'mission-control-backend'), '--data-dir', str(data / 'workspace')],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8')
            replies = queue.Queue()
            def receive():
                for line in process.stdout:
                    try: replies.put(json.loads(line))
                    except ValueError: pass
                replies.put(None)
            threading.Thread(target=receive, daemon=True).start()
            def wait_for(predicate):
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    item = replies.get(timeout=max(.1, deadline - time.monotonic()))
                    if item is None or item.get('event') == 'fatal': raise RuntimeError('Bundled backend stopped before replying')
                    if predicate(item):
                        if 'error' in item: raise RuntimeError('Bundled backend rejected the smoke request')
                        return item
                raise TimeoutError('Bundled backend response deadline exceeded')
            wait_for(lambda item: item.get('event') == 'ready')
            calls = iter(range(1, 1_000_000))
            def rpc(method, args=None):
                request = str(next(calls))
                process.stdin.write(json.dumps({'id': request, 'method': method, 'args': args or {}}) + '\n'); process.stdin.flush()
                return wait_for(lambda item: item.get('id') == request)['result']
            history = data / 'history'
            expected = write_synthetic_history(history)
            for project, name in SAMPLE_PROJECTS:
                rpc('saveProject', {'project': {'id': project, 'name': name, 'color': 'blue', 'description': 'Synthetic test history', 'rootPath': str(history / 'projects' / project)}})
                rpc('projects.approve', {'projectId': project, 'approved': True})
            rpc('saveSource', {'source': {'id': 'synthetic-claude', 'kind': 'claude', 'name': 'Synthetic Claude Code history', 'rootPath': str(history / 'sessions'), 'enabled': True, 'environment': 'macos'}})
            deadline = time.monotonic() + 60
            snapshot = rpc('snapshot')
            while snapshot['totalRecords'] != expected and time.monotonic() < deadline:
                time.sleep(.2); snapshot = rpc('snapshot')
            if snapshot['totalRecords'] != expected: raise RuntimeError(f"Synthetic indexing produced {snapshot['totalRecords']} of {expected} messages")
            config = rpc('mcpConfig')
            if Path(config['executablePath']).resolve() != binaries / 'mission-control-mcp': raise RuntimeError('Dashboard returned wrong MCP path')
            if Path(config['configurationPath']).resolve() != integrations: raise RuntimeError('Dashboard returned wrong setup resources path')
            rpc('monitoring.pause'); rpc('shutdown'); process.stdin.close(); process.wait(timeout=15)
            if process.returncode != 0: raise RuntimeError('Bundled backend did not stop cleanly')
            report['syntheticMessages'] = snapshot['totalRecords']; report['backendAndSqliteVerified'] = True
            sys.path.insert(0, str(integrations))
            import manage
            from verify_memory_package import verify
            report['mcp'] = verify(binaries / 'mission-control-mcp', data / 'workspace', manage.VERSION)
            report['version'] = manage.VERSION
            for directory in [data / 'workspace', data / 'workspace' / 'live']:
                if directory.stat().st_mode & 0o077: raise RuntimeError('Workspace directory is accessible to other local users')
            report['newWorkspacePermissionsVerified'] = True
        report['passed'] = True
    except (OSError, RuntimeError, subprocess.SubprocessError, queue.Empty, TimeoutError) as error:
        report['failure'] = type(error).__name__ + ': ' + str(error)
    finally:
        if process is not None and process.poll() is None:
            process.kill(); process.wait(timeout=5)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))
    return 0 if report['passed'] else 1


if __name__ == '__main__': raise SystemExit(main())
