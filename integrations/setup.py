#!/usr/bin/env python3
"""Prepare machine-local host packages on Windows/macOS; --install registers selected hosts.

Run after opening the desktop once. No history, tokens or external provider registrations
are copied. Python 3.11+ is needed for setup only, never for normal collection or recall.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tomllib
from types import SimpleNamespace

import manage
from verify_memory_package import verify


def default_data(platform=None, env=None, home=None):
    platform, env, home = platform or sys.platform, os.environ if env is None else env, Path.home() if home is None else Path(home)
    if env.get('MISSION_CONTROL_DATA_DIR'):
        return Path(env['MISSION_CONTROL_DATA_DIR'])
    if platform == 'darwin':
        return home / 'Library/Application Support/MissionControl'
    if platform == 'win32':
        return Path(env.get('LOCALAPPDATA', home / 'AppData/Local')) / 'MissionControl'
    return Path(env.get('XDG_DATA_HOME', home / '.local/share')) / 'MissionControl'


def bundled_executable(script_directory=None, platform=None):
    directory = Path(script_directory or Path(__file__).resolve().parent)
    name = 'mission-control-mcp.exe' if (platform or sys.platform) == 'win32' else 'mission-control-mcp'
    if directory.parent.name == 'Resources' and directory.parent.parent.name == 'Contents':
        return directory.parent.parent / 'MacOS' / name
    return directory.parent / name


def host_command(value, default):
    found = shutil.which(value or default)
    if not found:
        raise ValueError(f'{default} CLI not found. Install it or pass its --{default.split(".")[0]}-command path.')
    return found


def native(command):
    # Do not capture host stdout/stderr: native login/trust remains in the user's terminal.
    result = subprocess.run(command, check=False)
    if result.returncode:
        raise RuntimeError('Native host registration failed. Prepared packages remain available; fix the reported host error and rerun setup.')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mcp', type=Path, default=bundled_executable())
    parser.add_argument('--data-dir', type=Path, default=default_data())
    parser.add_argument('--output', type=Path, help='Stable writable package directory; defaults to DATA_DIR/host-packages')
    parser.add_argument('--host', choices=['claude', 'codex', 'both'], default='both')
    parser.add_argument('--install', action='store_true', help='Also register selected hosts; otherwise only prepare packages')
    parser.add_argument('--time-zone', help='local or an IANA timezone; preserves an existing choice')
    parser.add_argument('--private-slack', choices=['ask', 'public', 'allow'], help='Explicit consent choice; new installs default to ask')
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME', Path.home() / '.codex')))
    parser.add_argument('--codex-command')
    parser.add_argument('--claude-command')
    args = parser.parse_args(argv)
    try:
        data = manage.absolute(args.data_dir, 'Data directory')
        mcp = manage.absolute(args.mcp, 'MCP executable')
        output = manage.absolute(args.output or data / 'host-packages', 'Package directory')
        codex_home = manage.absolute(args.codex_home, 'Codex home')
        if not mcp.is_file():
            raise ValueError('MCP executable not found. Install the complete app first, or pass --mcp with its absolute path.')
        if not (data / 'live/workspace.db').is_file():
            raise ValueError('Open Mission Control once to create the local workspace, or select its existing --data-dir.')
        previous = json.loads((output / 'installation.json').read_text(encoding='utf-8')) if (output / 'installation.json').exists() else {}
        github_token_env = previous.get('githubTokenEnv', 'MISSION_CONTROL_GITHUB_TOKEN')
        if previous.get('github') and 'githubTokenEnv' not in previous:
            fragment = tomllib.loads((output / 'codex/config.fragment.toml').read_text(encoding='utf-8'))
            github_token_env = fragment['mcp_servers']['mc_github']['bearer_token_env_var']
        # Existing optional provider fragments are owned by manage.py; keep them intact on update.
        slack_client_id = None
        if previous.get('slackInternalApp'):
            external = json.loads((output / 'claude/external.mcp.json').read_text(encoding='utf-8'))
            slack_client_id = external['mcpServers']['mc_slack']['oauth']['clientId']
        commands = {}
        if args.install:
            if args.host in ('both', 'claude'):
                commands['claude'] = host_command(args.claude_command, 'claude')
            if args.host in ('both', 'codex'):
                commands['codex'] = host_command(args.codex_command, 'codex.cmd' if sys.platform == 'win32' else 'codex')
        verify(mcp, data, manage.VERSION)  # Fail before changing host files.
        manage.prepare(SimpleNamespace(output=str(output), mcp=str(mcp), data_dir=str(data),
            github=previous.get('github', False), github_token_env=github_token_env, slack_client_id=slack_client_id,
            private_slack=args.private_slack, time_zone=args.time_zone))
        if 'codex' in commands:
            # Native CLI and explicit configuration must refer to the same customized home.
            codex_home.mkdir(parents=True, exist_ok=True, mode=0o700)
            os.environ['CODEX_HOME'] = str(codex_home)
            native([commands['codex'], 'plugin', 'marketplace', 'add', str(output / 'codex')])
            native([commands['codex'], 'plugin', 'add', 'mission-control@mission-control-local'])
            manage.modify_config(SimpleNamespace(host='codex', bundle=str(output), target=str(codex_home / 'config.toml')))
        if 'claude' in commands:
            native([commands['claude'], 'plugin', 'install', 'mission-control', '--marketplace', str(output / 'claude'), '--scope', 'user'])
            native([commands['claude'], 'plugin', 'update', 'mission-control@mission-control-local', '--scope', 'user'])
        print(json.dumps({'version': manage.VERSION, 'packages': str(output), 'registeredHosts': list(commands),
            'next': 'Reload the selected hosts. Authenticate GitHub/Slack separately with your own accounts.' if commands else
                    'Packages prepared only. Rerun with --install --host claude, codex or both to register.'}))
        return 0
    except (ValueError, RuntimeError, OSError, KeyError) as error:
        print('Mission Control setup: ' + str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
