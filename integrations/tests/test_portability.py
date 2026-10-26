import importlib.util
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'integrations'))
import setup
import manage
import verify_memory_package

spec = importlib.util.spec_from_file_location('source_package', ROOT / 'scripts/package-source.py')
source_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(source_package)


class PortabilityTests(unittest.TestCase):
    def test_mac_paths_follow_recipient_and_support_explicit_data(self):
        home = Path('/Users/a different teammate')
        self.assertEqual(home / 'Library/Application Support/MissionControl', setup.default_data('darwin', {}, home))
        self.assertEqual(Path('/Volumes/Private/Memory'), setup.default_data('darwin', {'MISSION_CONTROL_DATA_DIR': '/Volumes/Private/Memory'}, home))
        script = Path('/Applications/Mission Control.app/Contents/Resources/integrations')
        self.assertEqual(Path('/Applications/Mission Control.app/Contents/MacOS/mission-control-mcp'), setup.bundled_executable(script, 'darwin'))

    def test_fresh_setup_generates_only_recipient_paths_without_inheriting_consent(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / 'live').mkdir()
            (root / 'live/workspace.db').touch()
            mcp = root / 'mission-control-mcp'; mcp.touch()
            with patch.object(setup, 'verify') as verify, patch.object(setup, 'native') as native:
                self.assertEqual(0, setup.main(['--mcp', str(mcp), '--data-dir', str(root)]))
                verify.assert_called_once(); native.assert_not_called()
            state = json.loads((root / 'host-packages/installation.json').read_text())
            self.assertEqual('local', state['timeZone'])
            self.assertEqual('ask', state['privateSlack'])
            server = json.loads((root / 'host-packages/claude/.mcp.json').read_text())['mcpServers']['history']
            self.assertEqual(str(mcp), server['command']); self.assertIn(str(root), server['args'])

    def test_preflight_failure_does_not_change_host_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve(); (root / 'live').mkdir()
            (root / 'live/workspace.db').touch(); mcp = root / 'mission-control-mcp'; mcp.touch()
            with patch.object(setup, 'verify', side_effect=RuntimeError('synthetic incompatible package')):
                self.assertEqual(2, setup.main(['--mcp', str(mcp), '--data-dir', str(root)]))
            self.assertFalse((root / 'host-packages').exists())

    def test_setup_update_preserves_optional_providers_timezone_and_consent(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve(); (root / 'live').mkdir()
            (root / 'live/workspace.db').touch(); mcp = root / 'mission-control-mcp'; mcp.touch()
            output = root / 'host-packages'
            manage.prepare(SimpleNamespace(output=str(output), mcp=str(mcp), data_dir=str(root), github=True,
                github_token_env='TEAM_GH_TOKEN', slack_client_id='123.456', private_slack='public', time_zone='America/New_York'))
            with patch.object(setup, 'verify'):
                self.assertEqual(0, setup.main(['--mcp', str(mcp), '--data-dir', str(root)]))
            state = json.loads((output / 'installation.json').read_text())
            self.assertEqual('America/New_York', state['timeZone']); self.assertEqual('public', state['privateSlack'])
            self.assertTrue(state['github']); self.assertTrue(state['slackInternalApp'])
            self.assertEqual('TEAM_GH_TOKEN', state['githubTokenEnv'])
            config = json.loads((output / 'claude/external.mcp.json').read_text())
            self.assertEqual('123.456', config['mcpServers']['mc_slack']['oauth']['clientId'])

    def test_source_archive_excludes_machine_state_and_uses_portable_names(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ['src/app.cs', 'src/.env', '.aws/credentials', '.vs/state', 'artifacts/private-history.json',
                         'src/obj/build.json', 'ui/node_modules/local.js', 'integrations/__pycache__/a.pyc',
                         'src-tauri/binaries/mission-control-mcp.exe', 'docs/team.md']:
                path = root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text('synthetic')
            output = root / 'out.zip'; source_package.package(root, output)
            with zipfile.ZipFile(output) as archive:
                self.assertEqual(['docs/team.md', 'src/app.cs'], sorted(archive.namelist()))
            with self.assertRaises(FileExistsError): source_package.package(root, output)

    def test_native_codex_appended_tables_are_preserved_without_adopting_edits(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve(); mcp = root / 'mcp'; mcp.touch(); output = root / 'bundle'
            manage.prepare(SimpleNamespace(output=str(output), mcp=str(mcp), data_dir=str(root), github=False,
                github_token_env='TOKEN_VAR', slack_client_id=None))
            target = root / 'config.toml'; args = SimpleNamespace(host='codex', bundle=str(output), target=str(target))
            manage.modify_config(args)
            original = target.read_text()
            host_tables = '\n[plugins."mission-control@mission-control-local"]\nenabled = true\n'
            target.write_text(original.replace(manage.END, host_tables + manage.END))
            manage.modify_config(args)
            self.assertIn(host_tables.strip(), target.read_text())
            owned = target.read_text().split(manage.BEGIN, 1)[1].split(manage.END, 1)[0]
            self.assertNotIn('[plugins.', owned)
            target.write_text(original.replace('--live', '--demo').replace(manage.END, host_tables + manage.END))
            with self.assertRaises(ValueError): manage.modify_config(args)
            self.assertIn('--demo', target.read_text())

    @unittest.skipIf(os.name == 'nt', 'Native Unix executable permissions require macOS/Linux')
    def test_executable_preflight_preserves_unix_execute_bit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); executable = root / 'mission-control-mcp'
            executable.write_text('#!/bin/sh\nread request\nprintf \'%s\\n\' \'{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"test"}}}\'\n')
            executable.chmod(stat.S_IRUSR | stat.S_IWUSR | stat.S_IXUSR)
            self.assertTrue(verify_memory_package.verify(executable, root, 'test')['standaloneExecutableVerified'])


if __name__ == '__main__': unittest.main()
