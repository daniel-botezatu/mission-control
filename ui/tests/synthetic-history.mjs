import { appendFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

// Synthetic Claude Code history for isolated app tests. The sample conversations are written as
// ordinary session files inside the test folder, then collected through the live pipeline after
// the test explicitly approves their project folders. Nothing outside the test folder is read.
const fixtures = fileURLToPath(new URL("../../fixtures/", import.meta.url));
const projects = [
  ["atlas", "Atlas API", "#89a8ff"],
  ["studio", "Studio", "#c69bf2"],
  ["orbit", "Orbit mobile", "#70c6b0"],
  ["dispatch", "Dispatch", "#e7b477"],
  ["platform", "Platform", "#86c1d5"],
];
// The samples are dated around this moment; they are moved to end shortly before the test runs.
const sampleReference = Date.parse("2026-10-16T10:00:00Z");

async function writeSample(file, root, shift, seen) {
  const rows = (await readFile(path.join(fixtures, file), "utf8"))
    .split("\n")
    .filter((line) => line.trim())
    .map((line) => JSON.parse(line));
  for (const row of rows) {
    const cwd = path.join(root, "projects", row.project);
    const timestamp = new Date(Date.parse(row.at) + shift).toISOString();
    const message =
      row.author === "user"
        ? { role: "user", content: row.text }
        : { role: "assistant", id: `msg-${row.id}`, stop_reason: "end_turn", content: [{ type: "text", text: row.text }] };
    const line = { type: row.author, uuid: row.id, sessionId: row.session, cwd, gitBranch: row.branch, timestamp, message };
    const sessionFile = path.join(root, "sessions", row.project, `${row.session}.jsonl`);
    await mkdir(path.dirname(sessionFile), { recursive: true });
    await appendFile(sessionFile, JSON.stringify(line) + "\n", "utf8");
  }
  // A repeated message id revises that message in place, so only new ids add a record.
  const added = rows.filter((row) => !seen.has(row.id)).length;
  rows.forEach((row) => seen.add(row.id));
  return added;
}

/** Writes the first sample day under `root`; `appendLater()` adds follow-up messages and revises two replies. */
export async function writeSyntheticHistory(root) {
  const shift = Date.now() - 30 * 60_000 - sampleReference;
  const seen = new Set();
  for (const [id] of projects) await mkdir(path.join(root, "projects", id), { recursive: true });
  const history = {
    root,
    sessions: path.join(root, "sessions"),
    projects: projects.map(([id, name, color]) => ({ id, name, color, rootPath: path.join(root, "projects", id) })),
    count: await writeSample("sample-history.jsonl", root, shift, seen),
    async appendLater() {
      const added = await writeSample("sample-history-later.jsonl", root, shift, seen);
      history.count += added;
      return added;
    },
  };
  return history;
}

/** Approves the sample projects, enables their session folder and waits until every message is indexed. */
export async function seedSyntheticHistory(rpc, history, timeoutMs = 60_000) {
  for (const project of history.projects) {
    await rpc("saveProject", { project: { ...project, description: "Synthetic test history" } });
    await rpc("projects.approve", { projectId: project.id, approved: true });
  }
  await rpc("saveSource", {
    source: {
      id: "synthetic-claude",
      kind: "claude",
      name: "Synthetic Claude Code history",
      rootPath: history.sessions,
      enabled: true,
      environment: process.platform === "darwin" ? "macos" : "windows",
    },
  });
  return waitForRecords(rpc, history.count, timeoutMs);
}

export async function waitForRecords(rpc, count, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  let snapshot = await rpc("snapshot");
  while (snapshot.totalRecords !== count && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 200));
    snapshot = await rpc("snapshot");
  }
  if (snapshot.totalRecords !== count)
    throw new Error(`Expected ${count} indexed synthetic messages, found ${snapshot.totalRecords}.`);
  return snapshot;
}
