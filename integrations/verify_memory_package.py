"""Fail before installation if the MCP executable needs unstaged dependencies."""
import argparse, json, queue, shutil, subprocess, tempfile, threading, time
from pathlib import Path

def verify(executable, data, version):
    request = {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
        'protocolVersion': '2025-11-25', 'capabilities': {},
        'clientInfo': {'name': 'mission-control-package-preflight', 'version': version}}}
    with tempfile.TemporaryDirectory(prefix='mission-control-package-') as temporary:
        isolated = Path(temporary)/executable.name
        shutil.copy2(executable, isolated)  # Keep executable permission on macOS/Linux.
        process = subprocess.Popen([str(isolated), '--data-dir', str(data), '--live'],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                   text=True, encoding='utf-8', cwd=temporary,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        replies = queue.Queue()
        def receive():
            for line in process.stdout:
                try: replies.put(json.loads(line))
                except ValueError: pass
            replies.put(None)
        reader = threading.Thread(target=receive, daemon=True); reader.start()
        valid = False
        try:
            process.stdin.write(json.dumps(request)+'\n'); process.stdin.flush()
            deadline = time.monotonic()+25
            while time.monotonic() < deadline:
                reply = replies.get(timeout=max(.1, deadline-time.monotonic()))
                if reply is None: break
                if reply.get('id') == 1:
                    valid = reply.get('result', {}).get('serverInfo', {}).get('version') == version
                    break
        except (queue.Empty, BrokenPipeError): pass
        finally:
            process.stdin.close()
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
            reader.join(timeout=2)
            process.stdout.close()
        if not valid:
            raise RuntimeError('Standalone memory-server preflight failed. No installed configuration was changed. Check bundled dependencies and database compatibility.')
    return {'standaloneExecutableVerified': True, 'version': version, 'databaseWritesRequested': False}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--data-dir', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.exe.resolve(), args.data_dir.resolve(), args.version)))
