#!/usr/bin/env python3
"""Create a portable source archive from an explicit source allowlist, never a user profile."""
import argparse
from pathlib import Path
import os
import zipfile
import hashlib
import stat

DIRECTORIES = {'src', 'src-tauri', 'ui', 'tests', 'scripts', 'integrations', 'fixtures', 'docs', '.github'}
FILES = {'README.md', 'ARCHITECTURE.md', 'BENCHMARKS.md', 'FINAL_INTEGRATION_CHECKLIST.md',
         'KNOWN_LIMITATIONS.md', 'RESEARCH.md', 'Directory.Build.props', 'global.json', 'MissionControl.slnx', '.gitignore'}
EXCLUDE = {'node_modules', 'bin', 'obj', 'target', 'binaries', 'gen', 'dist', '__pycache__',
           'TestResults', 'test-results', 'playwright-report', '.local', '.git', '.vs', '.aws', '.codex', '.claude', '.agents',
           '.ssh', '.idea', '.vscode', 'host-packages'}
PRIVATE_FILES = {'.DS_Store', 'Thumbs.db', 'desktop.ini', '.mcp.json', '.claude.json'}
GENERATED_SUFFIXES = {'.db', '.sqlite', '.sqlite3', '.pyc', '.pfx', '.p12', '.pem', '.key', '.user',
                      '.log', '.tmp', '.bak', '.zip', '.exe', '.dll', '.pdb', '.dmg'}
DESKTOP_ICONS = {'icon.ico', 'icon.icns', 'icon.png', 'icon.svg', 'tray-template.png'}


def excluded_file(path, root):
    name = path.name
    relative = path.relative_to(root)
    if relative.parts[:2] == ('src-tauri', 'icons') and (len(relative.parts) != 3 or name not in DESKTOP_ICONS):
        return True
    if name.endswith(('.jsonl', '.jsonl.zst')) and relative.parts[0] != 'fixtures':
        return True
    return (name in PRIVATE_FILES or name.startswith(('.env', '.mission-control-'))
            or name.endswith('.mission-control-owned.json') or path.suffix.lower() in GENERATED_SUFFIXES
            or name.endswith(('.db-wal', '.db-shm', '.db-journal', '.sqlite-wal', '.sqlite-shm', '.sqlite3-wal', '.sqlite3-shm')))


def redirected(path):
    metadata = path.lstat()
    return stat.S_ISLNK(metadata.st_mode) or bool(getattr(metadata, 'st_file_attributes', 0) & 0x400)


def source_files(root):
    root = Path(root)
    for item in sorted(root.iterdir()):
        if redirected(item):
            continue
        if item.is_file() and item.name in FILES:
            yield item
        elif item.is_dir() and item.name in DIRECTORIES:
            for directory, children, names in os.walk(item, followlinks=False):
                children[:] = sorted(name for name in children if name not in EXCLUDE and not redirected(Path(directory) / name))
                for name in sorted(names):
                    path = Path(directory) / name
                    if redirected(path) or excluded_file(path, root):
                        continue
                    yield path


def package(root, output):
    root, output = Path(root).resolve(), Path(output).absolute()
    output.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive create protects earlier release artifacts.
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in source_files(root):
            if path.resolve() == output.resolve():
                continue
            archive.write(path, path.relative_to(root).as_posix())
    return hashlib.sha256(output.read_bytes()).hexdigest()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(package(Path(__file__).resolve().parents[1], args.output) + '  ' + str(args.output))
