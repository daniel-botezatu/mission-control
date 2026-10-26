"""Read-only full-stream vs partition delivery comparison; never log bodies."""
import argparse, hashlib, json, queue, subprocess, threading, time
from pathlib import Path

def run():
    cli = argparse.ArgumentParser()
    for name in ['exe', 'data', 'from', 'before', 'report']: cli.add_argument('--'+name, required=True)
    a = cli.parse_args()
    p = subprocess.Popen([a.exe, '--data-dir', a.data, '--live'], stdin=subprocess.PIPE,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
                         creationflags=subprocess.CREATE_NO_WINDOW)
    q = queue.Queue()
    def receive():
        for line in p.stdout:
            try: q.put((json.loads(line), len(line)))
            except ValueError: pass
    threading.Thread(target=receive, daemon=True).start()
    sequence = 0
    def rpc(method, params):
        nonlocal sequence
        sequence += 1
        p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params})+'\n'); p.stdin.flush()
        while True:
            item, size = q.get(timeout=40)
            if item.get('id') == sequence:
                assert 'error' not in item, 'Protocol error; content omitted'
                return item['result'], size
    def call(params):
        result, size = rpc('tools/call', {'name': 'get_project_activity', 'arguments': params})
        assert not result.get('isError'), 'History changed or unavailable; refresh and retry'
        return json.loads(result['content'][0]['text']), size
    bounds = {'from': getattr(a, 'from'), 'before': a.before, 'timeZone': 'Europe/Amsterdam', 'limit': 50, 'maxOutputCharacters': 48000}
    def read(assignments):
        lengths = {}; hashes = {}; pages = 0; wire = 0; context_work = 0; sizes = []
        started = time.monotonic()
        for assignment in assignments:
            params = dict(assignment); cumulative = 0
            while True:
                page, size = call(params); pages += 1; wire += size; sizes.append(size)
                cumulative += size; context_work += cumulative
                for group in page['conversations']:
                    for row in group['messages']:
                        key = (group['sourceId'], group['sessionId'], group.get('projectId'), row[0])
                        assert lengths.get(key, 0) == row[4], 'Duplicate/missing body offset'
                        lengths[key] = row[4] + len(row[3].encode('utf-16-le'))//2
                        hashes.setdefault(key, hashlib.sha256()).update(row[3].encode('utf-16-le'))
                        if row[6] is None: assert lengths[key] == row[5]
                if page['nextCursor'] is None:
                    assert page['deliveryComplete'] and not page['historyChanged']
                    break
                params['cursor'] = page['nextCursor']
        fingerprint = hashlib.sha256(json.dumps(sorted((key, value.hexdigest()) for key, value in hashes.items())).encode()).hexdigest()
        return {'messages': len(lengths), 'pages': pages, 'wireCharacters': wire, 'bodyFingerprint': fingerprint,
                'cumulativeResponseCharacters': context_work, 'maximumPageCharacters': max(sizes, default=0),
                'seconds': round(time.monotonic()-started, 3)}
    report = {'passed': False, 'messageBodiesLogged': False}
    try:
        init, _ = rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'recap-delivery-check', 'version': '0.4.9'}})
        report['version'] = init['serverInfo']['version']
        p.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'})+'\n'); p.stdin.flush()
        report['fullStream'] = read([{**bounds, 'view': 'messages', 'format': 'compact', 'bodyCharacters': 48000}])
        manifest = []; revision = None; offset = 0
        while True:
            page, _ = call({**bounds, 'view': 'partitions', 'offset': offset})
            revision = revision or page['manifestRevision']; assert revision == page['manifestRevision']
            manifest.extend(page['partitions']); offset = page['nextOffset']
            if offset is None: break
        report['partitions'] = len(manifest); report['expectedMessages'] = page['totalMessages']
        report['partitioned'] = read([part['readArguments'] for part in manifest])
        final, _ = call({**bounds, 'view': 'partitions'})
        assert revision == final['manifestRevision'], 'Manifest changed during comparison'
        assert report['fullStream']['bodyFingerprint'] == report['partitioned']['bodyFingerprint']
        assert report['fullStream']['messages'] == report['partitioned']['messages'] == report['expectedMessages']
        report['passed'] = True
        report['usageNotice'] = 'Character accumulation excludes prompts, ledgers, merge and caching; it is not model token usage or billed cost.'
    finally:
        p.stdin.close()
        try: p.wait(timeout=10)
        except subprocess.TimeoutExpired: p.terminate(); p.wait(timeout=10)
        Path(a.report).write_text(json.dumps(report, indent=2)); print(json.dumps(report))

if __name__ == '__main__': run()
