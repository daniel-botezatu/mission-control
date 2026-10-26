import copy
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import tomllib

INTEGRATIONS = Path(__file__).parents[1]
sys.path.insert(0, str(INTEGRATIONS))
import manage
spec = importlib.util.spec_from_file_location("configure_github_provider", INTEGRATIONS / "configure_github_provider.py")
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)


class GitHubProviderConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.extension = ".exe" if os.name == "nt" else ""
        for name in ["gh", "github-mcp-server", "mission-control-providers"]:
            (self.root / (name + self.extension)).write_bytes(b"synthetic-not-executable")

    def tearDown(self):
        self.temporary.cleanup()

    def args(self, host):
        return SimpleNamespace(host=host, target=str(self.root / ("config.toml" if host == "codex" else "claude.json")),
                               launcher=str(self.root / ("mission-control-providers" + self.extension)), gh=str(self.root / ("gh" + self.extension)),
                               server=str(self.root / ("github-mcp-server" + self.extension)), server_sha256=manage.digest(b"synthetic-not-executable"))

    def old(self, host):
        result = {"command": str(self.root / ("github-mcp-server" + self.extension)), "args": ["stdio", "--read-only", "--toolsets=", "--tools=" + ",".join(provider.TOOLS), "--oauth-scopes=repo,read:org"]}
        if host == "codex":
            result["enabled_tools"] = provider.TOOLS.copy()
        else:
            result.update(type="stdio", env={})
        return result

    def toml_github(self):
        old = self.old("codex")
        return "[mcp_servers.mc_github]\n" + "\n".join(name + " = " + json.dumps(value) for name, value in old.items()) + "\n"

    def write(self, host):
        args = self.args(host)
        if host == "claude":
            document = {"theme": "dark", "mcpServers": {"mc_github": self.old(host), "mc_slack": {"type": "http", "url": "https://mcp.slack.com/mcp", "oauth": {"clientId": "synthetic-existing-client"}}, "other": {"command": "unrelated", "env": {"KEEP_TOKEN": "synthetic-unrelated-secret"}}}}
            Path(args.target).write_text(json.dumps(document), encoding="utf-8")
        else:
            Path(args.target).write_text('model = "existing"\n' + self.toml_github() + '\n# Keep Slack setup exactly\n[mcp_servers.mc_slack]\nurl = "https://mcp.slack.com/mcp"\n[mcp_servers.mc_slack.oauth]\nclient_id = "synthetic-existing-client"\n[mcp_servers.other.env]\nKEEP_TOKEN = "synthetic-unrelated-secret"\n', encoding="utf-8")
        return args

    def read(self, args):
        text = Path(args.target).read_text(encoding="utf-8")
        return tomllib.loads(text) if args.host == "codex" else json.loads(text)

    def test_changes_only_github_in_both_hosts_and_is_idempotent(self):
        for host in ["claude", "codex"]:
            with self.subTest(host=host):
                args = self.write(host)
                original = self.read(args)
                report = provider.configure(args)
                updated = self.read(args)
                key = "mcpServers" if host == "claude" else "mcp_servers"
                self.assertEqual(provider.replacement(args), updated[key]["mc_github"])
                original[key].pop("mc_github")
                updated[key].pop("mc_github")
                self.assertEqual(original, updated)
                self.assertTrue(report["unrelatedSettingsPreserved"])
                self.assertFalse(manage.config_state(Path(args.target)).exists())
                before_second = Path(args.target).read_bytes()
                provider.configure(args)
                self.assertEqual(before_second, Path(args.target).read_bytes())
                if host == "codex":
                    self.assertIn("# Keep Slack setup exactly", Path(args.target).read_text())

    def test_codex_preserves_managed_state_when_github_is_outside_owned_block(self):
        args = self.write("codex")
        target = Path(args.target)
        block = manage.BEGIN + '\n[mcp_servers.mission-control]\ncommand = "history.exe"\n' + manage.END + "\n"
        target.write_text(target.read_text() + block, encoding="utf-8")
        state = {"owner": manage.OWNER, "host": "codex", "blockHash": manage.digest(block.encode())}
        manage.write_atomic(manage.config_state(target), manage.dump(state))
        provider.configure(args)
        self.assertEqual(state, manage.read_state(target))
        self.assertIn(block, target.read_text())

    def test_codex_updates_owned_block_hash_when_github_is_inside(self):
        args = self.args("codex")
        target = Path(args.target)
        block = manage.BEGIN + "\n" + self.toml_github() + manage.END + "\n"
        target.write_text('model = "preserved"\n' + block, encoding="utf-8")
        state = {"owner": manage.OWNER, "host": "codex", "blockHash": manage.digest(block.encode())}
        manage.write_atomic(manage.config_state(target), manage.dump(state))
        provider.configure(args)
        actual = manage.read_state(target)
        self.assertNotEqual(state["blockHash"], actual["blockHash"])
        self.assertEqual(manage.digest(provider.managed_block(target.read_text()).group().encode()), actual["blockHash"])
        provider.configure(args)

    def test_claude_updates_only_owned_github_record(self):
        args = self.write("claude")
        target = Path(args.target)
        servers = self.read(args)["mcpServers"]
        state = {"owner": manage.OWNER, "host": "claude", "servers": copy.deepcopy(servers)}
        manage.write_atomic(manage.config_state(target), manage.dump(state))
        provider.configure(args)
        actual = manage.read_state(target)
        self.assertEqual(provider.replacement(args), actual["servers"]["mc_github"])
        self.assertEqual(state["servers"]["mc_slack"], actual["servers"]["mc_slack"])

    def launcher_versions(self):
        for version in ["0.4.8", "0.4.14"]:
            folder = self.root / "providers" / "mission-control" / version
            folder.mkdir(parents=True)
            (folder / ("mission-control-providers" + self.extension)).write_bytes(b"synthetic-not-executable")
        return [str(self.root / "providers" / "mission-control" / version / ("mission-control-providers" + self.extension)) for version in ["0.4.8", "0.4.14"]]

    def test_upgrades_a_launcher_from_another_version_folder_in_both_hosts(self):
        older, newer = self.launcher_versions()
        for host in ["claude", "codex"]:
            with self.subTest(host=host):
                args = self.write(host)
                args.launcher = older
                provider.configure(args)
                args.launcher = newer
                provider.configure(args)
                key = "mcpServers" if host == "claude" else "mcp_servers"
                self.assertEqual(newer, self.read(args)[key]["mc_github"]["command"])

    def test_launcher_upgrade_refuses_other_folders_or_changed_arguments(self):
        older, newer = self.launcher_versions()
        elsewhere = self.root / "elsewhere"
        elsewhere.mkdir()
        (elsewhere / ("mission-control-providers" + self.extension)).write_bytes(b"synthetic-not-executable")
        for command, args_change in [(str(elsewhere / ("mission-control-providers" + self.extension)), None), (older, ["github", "--gh-executable", "other"])]:
            with self.subTest(command=command, changed=args_change is not None):
                args = self.write("claude")
                args.launcher = older
                provider.configure(args)
                target = Path(args.target)
                document = self.read(args)
                document["mcpServers"]["mc_github"]["command"] = command
                if args_change:
                    document["mcpServers"]["mc_github"]["args"] = args_change
                target.write_text(json.dumps(document), encoding="utf-8")
                before = target.read_bytes()
                args.launcher = newer
                with self.assertRaises(ValueError):
                    provider.configure(args)
                self.assertEqual(before, target.read_bytes())

    def test_refuses_credentials_or_unrecognized_command_and_leaves_file_exact(self):
        for change in [{"env": {"GITHUB_PERSONAL_ACCESS_TOKEN": "synthetic-secret"}}, {"headers": {"Authorization": "synthetic-secret"}}, {"command": "unknown.exe"}, {"args": ["stdio", "--tools=create_issue"]}]:
            with self.subTest(change=list(change)):
                args = self.write("claude")
                target = Path(args.target)
                document = self.read(args)
                document["mcpServers"]["mc_github"].update(change)
                target.write_text(json.dumps(document), encoding="utf-8")
                before = target.read_bytes()
                with self.assertRaises(ValueError):
                    provider.configure(args)
                self.assertEqual(before, target.read_bytes())

    def test_refuses_unexpected_tool_filter(self):
        args = self.write("codex")
        target = Path(args.target)
        before = target.read_text().replace('enabled_tools = ["get_me",', 'enabled_tools = ["create_issue",')
        target.write_text(before)
        with self.assertRaises(ValueError):
            provider.configure(args)
        self.assertEqual(before, target.read_text())

    def test_refuses_checksum_mismatch_before_any_config_write(self):
        args = self.write("claude")
        before = Path(args.target).read_bytes()
        args.server_sha256 = "0" * 64
        with self.assertRaises(ValueError):
            provider.configure(args)
        self.assertEqual(before, Path(args.target).read_bytes())

    def test_refuses_edited_ownership_state(self):
        args = self.write("claude")
        target = Path(args.target)
        state = {"owner": manage.OWNER, "host": "claude", "servers": {"mc_slack": {"changed": True}}}
        manage.write_atomic(manage.config_state(target), manage.dump(state))
        before = target.read_bytes()
        with self.assertRaises(ValueError):
            provider.configure(args)
        self.assertEqual(before, target.read_bytes())

    def test_owned_journal_recovers_if_config_write_is_interrupted(self):
        args = self.write("claude")
        target = Path(args.target)
        original = target.read_bytes()
        state = {"owner": manage.OWNER, "host": "claude", "servers": {"mc_github": self.old("claude")}}
        manage.write_atomic(manage.config_state(target), manage.dump(state))
        write = manage.write_atomic
        def interrupted(path, text):
            if path == target:
                raise OSError("synthetic interruption")
            return write(path, text)
        with patch.object(manage, "write_atomic", side_effect=interrupted):
            with self.assertRaises(OSError):
                provider.configure(args)
        self.assertEqual(original, target.read_bytes())
        self.assertEqual(state, manage.read_state(target))


if __name__ == "__main__":
    unittest.main()
