#!/usr/bin/env python3
"""Switch recognized mc_github registrations to the pinned gh-login launcher; no credentials are read."""
from __future__ import annotations

import argparse
import copy
import json
import os
from pathlib import Path
import re
import sys
import tomllib

import manage

TOOLS = manage.TOOLS["github"]


def executable(value, filename):
    # Homebrew installs symlinks. Pin the explicit resolved executable, not the alias.
    original = Path(value)
    if not original.is_absolute():
        raise ValueError("Executable must be an absolute path")
    path = manage.absolute(original.resolve(strict=True), "Executable")
    if not path.is_file() or path.name.lower() != filename:
        raise ValueError("Expected an existing provider executable")
    return path


def replacement(args):
    suffix = ".exe" if os.name == "nt" else ""
    launcher = executable(args.launcher, "mission-control-providers" + suffix)
    gh = executable(args.gh, "gh" + suffix)
    server = executable(args.server, "github-mcp-server" + suffix)
    if not re.fullmatch(r"[a-fA-F0-9]{64}", args.server_sha256) or manage.digest(server.read_bytes()) != args.server_sha256.lower():
        raise ValueError("Pinned GitHub server checksum does not match")
    result = {"command": str(launcher), "args": ["github", "--gh-executable", str(gh), "--github-server", str(server), "--github-sha256", args.server_sha256.lower()]}
    if args.host == "codex":
        result["enabled_tools"] = TOOLS.copy()
    else:
        result = {"type": "stdio", **result}
    return result


def recognize(current, proposed, args):
    if not isinstance(current, dict):
        raise ValueError("An existing mc_github registration is required")
    allowed = {"command", "args", "enabled_tools"} if args.host == "codex" else {"type", "command", "args", "env"}
    if set(current) - allowed or current.get("env") or current.get("type", "stdio") != "stdio":
        raise ValueError("Unrecognized or authentication-bearing GitHub configuration; refusing replacement")
    if args.host == "codex" and current.get("enabled_tools", TOOLS) != TOOLS:
        raise ValueError("GitHub tool restrictions differ from the expected read-only set")
    if current == proposed or current == {**proposed, "env": {}}:
        return
    if current.get("command") == proposed["command"] and current.get("args") == proposed["args"]:
        return
    command = current.get("command")
    if not isinstance(command, str) or manage.absolute(command, "Existing server") != manage.absolute(args.server, "Pinned server"):
        raise ValueError("Existing GitHub command is not the recognized official local server")
    expected = ["stdio", "--read-only", "--toolsets=", "--tools=" + ",".join(TOOLS)]
    if current.get("args") not in (expected, expected + ["--oauth-scopes=repo,read:org"]):
        raise ValueError("Existing GitHub arguments differ from the recognized read-only configuration")


def managed_block(text):
    if text.count(manage.BEGIN) != text.count(manage.END) or text.count(manage.BEGIN) > 1:
        raise ValueError("Malformed managed configuration markers")
    return re.search(re.escape(manage.BEGIN) + r"\r?\n.*?" + re.escape(manage.END) + r"(?:\r?\n)?", text, re.S)


def block_hash(block):
    # manage.py owns this hash and reads text using Python's universal newline handling.
    return manage.digest(block.replace("\r\n", "\n").encode())


def update_codex(original, proposed, state):
    block = managed_block(original)
    if block and (not state or state.get("blockHash") != block_hash(block.group())):
        raise ValueError("Managed configuration was edited; refusing replacement")
    if state and not block:
        raise ValueError("Owned configuration block is missing")
    header = re.compile(r"(?m)^[ \t]*\[mcp_servers\.(?:mc_github|\"mc_github\"|'mc_github')\][ \t]*(?:#[^\r\n]*)?\r?\n")
    matches = list(header.finditer(original))
    if len(matches) != 1:
        raise ValueError("Expected one ordinary mc_github TOML table")
    start = matches[0].start()
    after_header = matches[0].end()
    following = re.search(r"(?m)^[ \t]*(?:\[|" + re.escape(manage.BEGIN) + "|" + re.escape(manage.END) + ")", original[after_header:])
    end = after_header + following.start() if following else len(original)
    # Leave trailing comments/blank lines with their original neighboring configuration.
    body = original[after_header:end]
    tail = re.search(r"(?:^[ \t]*(?:#[^\r\n]*)?\r?\n)+\Z", body, re.M)
    if tail:
        end = after_header + tail.start()
    newline = "\r\n" if "\r\n" in original else "\n"
    lines = ["[mcp_servers.mc_github]", "command = " + json.dumps(proposed["command"]), "args = " + json.dumps(proposed["args"]), "enabled_tools = " + json.dumps(TOOLS)]
    updated = original[:start] + newline.join(lines) + newline + original[end:]
    tomllib.loads(updated)
    if state:
        state = copy.deepcopy(state)
        new_block = managed_block(updated)
        state["blockHash"] = block_hash(new_block.group())
    return updated, state


def configure(args):
    target = manage.absolute(args.target, "Target configuration")
    state_path = manage.config_state(target)
    manage.safe_tree(state_path)
    if not target.is_file():
        raise ValueError("Existing host configuration is required")
    original_bytes = target.read_bytes()
    original = original_bytes.decode("utf-8-sig")
    state = manage.read_state(target)
    previous_state = copy.deepcopy(state)
    if state and state.get("host") != args.host:
        raise ValueError("Configuration ownership belongs to another host")
    document = tomllib.loads(original) if args.host == "codex" else json.loads(original)
    key = "mcp_servers" if args.host == "codex" else "mcpServers"
    if not isinstance(document, dict) or not isinstance(document.get(key), dict):
        raise ValueError("Expected a host MCP server map")
    servers = document.get(key, {})
    proposed = replacement(args)
    recognize(servers.get("mc_github"), proposed, args)
    if args.host == "codex":
        updated, state = update_codex(original, proposed, state)
    else:
        if state:
            for name, value in state.get("servers", {}).items():
                if servers.get(name) != value:
                    raise ValueError("An owned MCP registration was edited; refusing replacement")
        servers["mc_github"] = proposed
        if state and "mc_github" in state.get("servers", {}):
            state = copy.deepcopy(state)
            state["servers"]["mc_github"] = proposed
        updated = manage.dump(document)
        json.loads(updated)
    parsed = tomllib.loads(updated) if args.host == "codex" else json.loads(updated)
    before_without_github = copy.deepcopy(tomllib.loads(original) if args.host == "codex" else json.loads(original))
    after_without_github = copy.deepcopy(parsed)
    before_without_github[key].pop("mc_github")
    after_without_github[key].pop("mc_github")
    if before_without_github != after_without_github:
        raise ValueError("Unrelated host configuration changed; refusing write")
    if target.read_bytes() != original_bytes:
        raise ValueError("Host configuration changed during preparation; retry")
    # Journal only when a native ownership record already exists; never claim unrelated host entries.
    if state:
        manage.write_atomic(state_path, manage.dump({"owner": manage.OWNER, "pending": {
            "before": manage.digest(original_bytes), "after": manage.digest(updated.encode()), "previous": previous_state, "next": state}}))
    manage.write_atomic(target, updated)
    if state:
        manage.write_atomic(state_path, manage.dump(state))
    return {"host": args.host, "provider": "mc_github", "credentialStorage": "unchanged GitHub CLI login", "unrelatedSettingsPreserved": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, choices=["codex", "claude"])
    for argument in ["target", "launcher", "gh", "server", "server-sha256"]:
        parser.add_argument("--" + argument, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(configure(args)))
        return 0
    except (ValueError, OSError, KeyError, TypeError):
        print("GitHub provider configuration could not be completed. Verify the installation, checksum and recognized configuration, then retry; any pending owned write is recoverable. No configuration values were logged.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
