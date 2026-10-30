#!/usr/bin/env python3
"""Check a built Mac app with disposable demo data; never use the operator's workspace or accounts."""
import argparse
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
              'loginItemVerified': False, 'distributionChecksRequested': args.distribution}
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
            process = subprocess.Popen([str(binaries / 'mission-control-backend'), '--data-dir', str(data), '--demo'],
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
            def rpc(method):
                process.stdin.write(json.dumps({'id': method, 'method': method, 'args': {}}) + '\n'); process.stdin.flush()
                return wait_for(lambda item: item.get('id') == method)['result']
            snapshot = rpc('snapshot')
            if snapshot['mode'] != 'demo' or snapshot['totalRecords'] <= 0: raise RuntimeError('Synthetic indexing produced no demo records')
            config = rpc('mcpConfig')
            if Path(config['executablePath']).resolve() != binaries / 'mission-control-mcp': raise RuntimeError('Dashboard returned wrong MCP path')
            if Path(config['configurationPath']).resolve() != integrations: raise RuntimeError('Dashboard returned wrong setup resources path')
            rpc('monitoring.pause'); rpc('shutdown'); process.stdin.close(); process.wait(timeout=15)
            if process.returncode != 0: raise RuntimeError('Bundled backend did not stop cleanly')
            report['demoMessages'] = snapshot['totalRecords']; report['backendAndSqliteVerified'] = True
            sys.path.insert(0, str(integrations))
            import manage
            from verify_memory_package import verify
            report['mcp'] = verify(binaries / 'mission-control-mcp', data, manage.VERSION)
            report['version'] = manage.VERSION
            for directory in [data / 'live', data / 'demo']:
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
