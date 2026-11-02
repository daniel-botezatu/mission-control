import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { createInterface } from "node:readline";
import path from "node:path";

assert.ok(
  process.env.MC_MCP_EXECUTABLE,
  "Select the exact packaged MCP executable",
);
assert.ok(
  process.env.MC_NATIVE_TEST_DATA_ROOT,
  "Select the isolated workspace that native-smoke seeded, after its desktop has quit",
);
assert.ok(process.env.MC_UI_ARTIFACTS, "Select an explicit artifact directory");
const executable = path.resolve(process.env.MC_MCP_EXECUTABLE);
const dataDirectory = path.resolve(process.env.MC_NATIVE_TEST_DATA_ROOT);
const database = path.join(dataDirectory, "live", "workspace.db");
const hash = async (file) =>
  createHash("sha256")
    .update(await readFile(file))
    .digest("hex");
const report = {
  started: new Date().toISOString(),
  passed: false,
  executableSha256: await hash(executable),
  databaseBefore: await hash(database),
};
const child = spawn(executable, ["--data-dir", dataDirectory], {
  stdio: ["pipe", "pipe", "pipe"],
  windowsHide: true,
});
const pending = new Map();
let next = 0;
let stderrBytes = 0;
child.stderr.on("data", (chunk) => {
  stderrBytes += chunk.length;
});
const lines = createInterface({ input: child.stdout });
lines.on("line", (line) => {
  try {
    const value = JSON.parse(line);
    if (!pending.has(value.id)) return;
    const waiter = pending.get(value.id);
    pending.delete(value.id);
    clearTimeout(waiter.timer);
    if (value.error) waiter.reject(new Error(value.error.message));
    else waiter.resolve(value.result);
  } catch (error) {
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(error);
    }
    pending.clear();
  }
});
const exited = new Promise((resolve, reject) => {
  child.once("error", reject);
  child.once("exit", (code, signal) => {
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(new Error("MCP exited before replying"));
    }
    pending.clear();
    resolve({ code, signal });
  });
});
const rpc = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = ++next;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`MCP timeout: ${method}`));
    }, 15_000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(
      JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n",
    );
  });
try {
  const initialized = await rpc("initialize", {
    protocolVersion: "2025-11-25",
    capabilities: {},
    clientInfo: { name: "native-package-smoke", version: "1.0" },
  });
  assert.equal(initialized.serverInfo.name, "mission-control-memory");
  assert.equal(initialized.serverInfo.version, process.env.MC_EXPECTED_MCP_VERSION || "0.4.20");
  child.stdin.write(
    JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) +
      "\n",
  );
  const { tools } = await rpc("tools/list");
  assert.deepEqual(tools.map((tool) => tool.name).sort(), [
    "get_exchange",
    "get_project_activity",
    "get_session_details",
    "get_session_digest",
    "search_history",
  ]);
  for (const tool of tools) {
    assert.equal(tool.annotations.readOnlyHint, true);
    assert.equal(tool.annotations.destructiveHint, false);
  }
  const unwrap = (value) =>
    value.structuredContent ||
    JSON.parse(value.content.find((item) => item.type === "text").text);
  const call = async (name, args) => {
    const response = await rpc("tools/call", { name, arguments: args });
    assert.ok(!response.isError, `${name} returned an error`);
    return unwrap(response);
  };
  const result = await call("search_history", { query: "cursor", limit: 3 });
  assert.ok(result.records.length > 0 && result.records.length <= 3);
  const record = result.records[0];
  assert.ok(record.exchangeId && record.reference.recordId === record.id);
  assert.ok(["claude", "codex"].includes(record.sourceKind));
  const detail = await call("get_exchange", {
    exchangeId: record.exchangeId,
    sourceId: record.sourceId,
    sessionId: record.sessionId,
    limit: 4,
  });
  assert.ok(detail.records.some((value) => value.id === record.id));
  assert.ok(
    detail.records.every((value) => value.exchangeId === record.exchangeId),
  );
  const digest = await call("get_session_digest", {
    sourceId: record.sourceId,
    sessionId: record.sessionId,
    limit: 4,
  });
  assert.ok(digest.records.length > 0 && digest.records.length <= 4);
  assert.ok(
    digest.records.every((value) =>
      ["user", "assistant"].includes(value.author),
    ),
  );
  const activity = await call("get_project_activity", {
    projectId: record.projectId,
    limit: 4,
  });
  assert.ok(
    activity.sessions.length > 0 &&
      activity.sessions.every((value) => value.projectId === record.projectId),
  );
  const session = await call("get_session_details", {
    sourceId: record.sourceId,
    sessionId: record.sessionId,
  });
  assert.ok(session.sessions.length > 0);
  assert.ok(
    session.sessions.every(
      (value) =>
        !value.lastObserved || value.lastObserved.verifiedCompletion === false,
    ),
  );
  child.stdin.end();
  let timer;
  const exit = await Promise.race([
    exited,
    new Promise((_, reject) => {
      timer = setTimeout(
        () =>
          reject(new Error("MCP did not exit after its client disconnected")),
        10_000,
      );
    }),
  ]).finally(() => clearTimeout(timer));
  assert.equal(exit.code, 0);
  report.databaseAfter = await hash(database);
  assert.equal(
    report.databaseAfter,
    report.databaseBefore,
    "The packaged read-only MCP server must not mutate the closed workspace database",
  );
  report.passed = true;
  report.protocolVersion = initialized.protocolVersion;
  report.tools = tools.map((tool) => tool.name);
  report.recordId = record.id;
  report.exitCode = exit.code;
  console.log(
    "PASS Packaged MCP: initialize, five read-only memory tools, search, exchange, digest, project activity, session details, clean client-disconnect exit, unchanged database hash.",
  );
} catch (error) {
  report.error = error.stack;
  console.error(report.error);
  process.exitCode = 1;
} finally {
  if (child.exitCode === null) child.kill();
  lines.close();
  report.stderrBytes = stderrBytes;
  report.finished = new Date().toISOString();
  await writeFile(
    path.join(process.env.MC_UI_ARTIFACTS, "packaged-mcp-smoke-report.json"),
    JSON.stringify(report, null, 2),
  );
}
