import assert from "node:assert/strict";
import { createServer } from "node:http";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { chromium } from "playwright-core";

// Synthetic local-memory bridge only. No account APIs, credentials, or real history.
const root = path.resolve("ui/dist"),
  output = path.resolve("artifacts/memory-ui-flows");
await mkdir(output, { recursive: true });
const server = createServer(async (req, res) => {
  try {
    const pathname = decodeURIComponent(
      new URL(req.url, "http://localhost").pathname,
    );
    const file = path.resolve(
      root,
      pathname === "/" ? "index.html" : `.${pathname}`,
    );
    if (!file.startsWith(root + path.sep)) return res.writeHead(403).end();
    const data = await readFile(file);
    res
      .writeHead(200, {
        "Content-Type":
          {
            ".html": "text/html",
            ".js": "text/javascript",
            ".css": "text/css",
          }[path.extname(file)] || "application/octet-stream",
      })
      .end(data);
  } catch {
    res.writeHead(404).end();
  }
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const browser = await chromium.launch({ channel: "msedge", headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 960 } });
page.setDefaultTimeout(10000);
const calls = [],
  errors = [],
  checks = [];
page.on("pageerror", (e) => errors.push(e.message));
let paused = true;
let localUnavailable = false;
let localEmpty = false;
// Branch overflow must remain reachable without any GitHub account requests.
let loopsOverflow = false;
const overflowBranch = (n) => ({
  repository: "example/project",
  checkout: "C:/work/project",
  branch: `topic/branch-${n}`,
  state: n === 6 ? "unverified" : "ahead",
  commits: n === 6 ? null : n,
  lastCommitAt: `2026-10-21T0${9 - Math.min(n, 9)}:00:00Z`,
});
let extraStatsEntries = 0;
const projects = [
  {
    id: "sp",
    name: "SP",
    description: "Shared product workspace",
    color: "#648674",
    rootPath: "C:\\Work\\SP",
    isApproved: true,
    approvedAt: "2026-10-21T08:00:00Z",
    autoDiscovered: true,
    repositories: [
      {
        localRoot: "C:\\Work\\SP\\billing",
        gitHubRepositories: ["Acme/billing"],
      },
      {
        localRoot: "C:\\Work\\SP\\processor",
        gitHubRepositories: ["Acme/report-processor"],
      },
    ],
  },
  {
    id: "personal",
    name: "Personal candidate",
    description: "",
    color: "#987655",
    rootPath: "C:\\Personal",
    isApproved: false,
    autoDiscovered: true,
    repositories: [],
  },
];
const makeRecord = (id, extra = {}) => ({
  id,
  sourceId: "codex",
  streamId: "stream",
  sessionId: "session-1",
  projectId: "sp",
  sourceKind: "codex",
  kind: "message",
  title: "Investigate API response",
  body: "Find the original schema decision",
  author: "user",
  timestamp: "2026-10-21T09:00:00Z",
  changedAt: "2026-10-21T09:00:00Z",
  ingestedAt: "2026-10-21T09:00:00Z",
  workingDirectory: "C:\\Work\\SP",
  branch: "main",
  state: null,
  url: null,
  dataJson: null,
  ...extra,
});
let records = [
  makeRecord("question"),
  makeRecord("answer", {
    author: "assistant",
    body: "FINAL RESPONSE FROM SOURCE",
    timestamp: "2026-10-21T09:01:00Z",
  }),
  makeRecord("second", {
    sessionId: "session-2",
    sourceKind: "claude",
    title: "Check processor behavior",
    body: "Review stopped clock handling",
    state: "interrupted",
  }),
  makeRecord("private", {
    projectId: "personal",
    title: "PRIVATE HISTORY",
    body: "PERSONAL SOURCE BODY",
  }),
];
const corrections = [];
const longBodies = new Map();
let correctionBarrier;
const allowed = () =>
  records.filter((r) =>
    projects.some((p) => p.id === r.projectId && p.isApproved),
  );
let sourceGap = null;
const sources = [
  {
    id: "codex",
    name: "Codex local",
    kind: "codex",
    rootPath: "C:\\Users\\QA\\.codex\\sessions",
    enabled: true,
    environment: "windows",
  },
];
await page.exposeFunction("syntheticRpc", async ({ method, args }) => {
  calls.push({ method, args });
  if (method === "host.version") return { version: "0.4.3" };
  if (method === "collection.stats")
    return {
      mode: "live",
      monitoring: paused ? "paused" : "running",
      savedEntries: allowed().length + extraStatsEntries,
      sessions: 2,
      userMessages: 2 + extraStatsEntries,
      finalResponses: 1,
      statusEvents: 0,
      databaseBytes: 4096 + extraStatsEntries * 1024 * 1024,
      activeDatabaseBytes: 4096 + extraStatsEntries * 1024 * 1024,
      storageLimitBytes: 1024 * 1024 * 1024,
      lastCheckpoint: "2026-10-21T10:00:00Z",
      catchingUp: !paused,
      pendingFiles: 4,
      pendingBytes: 64 * 1024 * 1024,
    };
  if (method === "openLoops.local" && loopsOverflow)
    return {
      checkedAt: "2026-10-21T10:00:00Z",
      branches: [1, 2, 3, 4, 5, 6].map(overflowBranch),
      branchesOmitted: 2,
      drafts: [],
    };
  if (method === "openLoops.local" && localUnavailable)
    throw new Error("Local reminders could not be loaded.");
  if (method === "openLoops.local" && localEmpty)
    return { checkedAt: "2026-10-21T10:00:00Z", branches: [], drafts: [] };
  if (method === "openLoops.local")
    return {
      checkedAt: "2026-10-21T10:00:00Z",
      branches: [
        {
          repository: "example/project",
          checkout: "C:/work/project",
          branch: "fix/unpushed-work",
          state: "ahead",
          lastCommitAt: "2026-10-21T09:30:00Z",
        },
      ],
      drafts: [],
    };
  if (method === "snapshot")
    return {
      mode: "live",
      monitoring: paused ? "paused" : "watching",
      projects,
      records: allowed(),
      sources: sources.map((s) => ({
        ...s,
        state: paused ? "paused" : "watching",
        lastSynced: "2026-10-21T09:01:00Z",
        pending: 0,
        gap: sourceGap,
      })),
      corrections,
      totalRecords: allowed().length,
      sqliteVersion: "test",
      lastCheckpoint: "2026-10-21T09:01:00Z",
      databaseBytes: 4096,
    };
  if (method === "projects") return projects;
  if (method === "sources") return sources;
  if (method === "settings")
    return {
      retentionDays: 90,
      startWithWindows: false,
      telemetry: false,
      lookbackDays: 30,
      storageLimitMb: 1024,
      includeSubagents: false,
    };
  if (method === "mcpConfig")
    return {
      executablePath: "C:\\MissionControl\\mission-control-mcp.exe",
      dataDirectory: "C:\\Memory",
      claudeCommand:
        'claude mcp add mission-control -- "C:\\MissionControl\\mission-control-mcp.exe" --data-dir "C:\\Memory"',
      codexCommand:
        'codex mcp add mission-control -- "C:\\MissionControl\\mission-control-mcp.exe" --data-dir "C:\\Memory"',
    };
  if (["query", "sessions", "conversation"].includes(method)) {
    let list = allowed().filter(
      (r) =>
        (!args.projectId || r.projectId === args.projectId) &&
        (!args.sourceKind || r.sourceKind === args.sourceKind) &&
        (!args.sessionId || r.sessionId === args.sessionId) &&
        (!args.search ||
          (r.title + " " + r.body)
            .toLowerCase()
            .includes(args.search.toLowerCase())),
    );
    if (method === "sessions")
      list = [
        ...new Map(
          list.map((r) => [r.sourceId + ":" + r.sessionId, r]),
        ).values(),
      ];
    if (args.byTimestamp)
      list.sort(
        (a, b) =>
          b.timestamp.localeCompare(a.timestamp) || a.id.localeCompare(b.id),
      );
    return {
      records: list.slice(
        args.offset || 0,
        (args.offset || 0) + (args.limit || 50),
      ),
      total: list.length,
    };
  }
  if (method === "record") {
    const record = allowed().find((r) => r.id === args.id);
    const body = longBodies.get(args.id);
    if (!record || !body) return record;
    const offset = args.bodyOffset || 0,
      end = Math.min(body.length, offset + 12000);
    return {
      ...record,
      body: body.slice(offset, end),
      bodyLength: body.length,
      nextBodyOffset: end < body.length ? end : null,
    };
  }
  if (method === "recordCorrection")
    return corrections.find((c) => c.recordId === args.id) || null;
  if (method === "correction") {
    const old = corrections.findIndex(
      (c) => c.recordId === args.correction.recordId,
    );
    if (old >= 0) corrections.splice(old, 1);
    corrections.push(args.correction);
    if (correctionBarrier) {
      correctionBarrier.started();
      await correctionBarrier.wait;
    }
    return { saved: true };
  }
  if (method === "undoCorrection") {
    const index = corrections.findIndex((c) => c.recordId === args.recordId);
    if (index >= 0) corrections.splice(index, 1);
    return { saved: true };
  }
  if (method === "projects.approve") {
    const project = projects.find((p) => p.id === args.projectId);
    project.isApproved = args.approved;
    if (!args.approved) {
      const before = records.length;
      records = records.filter((r) => r.projectId !== project.id);
      return { purged: before - records.length };
    }
    return { purged: 0 };
  }
  if (method === "control") {
    if (args.action === "pause") paused = true;
    if (args.action === "resume") paused = false;
    return { ok: true };
  }
  if (method === "saveProject") {
    const index = projects.findIndex((p) => p.id === args.project.id);
    if (index >= 0)
      projects[index] = {
        ...projects[index],
        ...args.project,
        isApproved: projects[index].isApproved,
      };
    else projects.push({ ...args.project, isApproved: false });
    return { saved: true };
  }
  if (
    [
      "projects.discover",
      "saveSource",
      "saveSettings",
      "sessionProject",
      "retention",
    ].includes(method)
  )
    return { saved: true };
  throw new Error("Unexpected synthetic RPC: " + method);
});
await page.addInitScript(() => {
  window.__TAURI__ = {
    core: {
      invoke: async (command, { method, args }) => {
        if (command !== "workspace_rpc") throw new Error("Unknown command");
        return window.syntheticRpc({ method, args });
      },
    },
    event: {
      listen: async (_name, callback) => {
        window.syntheticChanged = callback;
        return () => {
          window.syntheticChanged = undefined;
        };
      },
    },
  };
});
const check = async (name, fn) => {
  await fn();
  checks.push({ name, passed: true });
  console.log("PASS " + name);
};
const nav = async (name) =>
  page.getByRole("button", { name, exact: true }).first().click();
try {
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await check(
    "Approved overview excludes candidate metadata and all builtin AI controls",
    async () => {
      await page
        .getByRole("heading", { name: "Pick up where you left off." })
        .waitFor();
      const body = await page.locator("body").innerText();
      assert.ok(!body.includes("Personal candidate"));
      assert.ok(!body.includes("PRIVATE HISTORY"));
      assert.equal(await page.locator(".ask-panel,.ask-trigger").count(), 0);
      assert.equal(
        await page.getByText("AI providers", { exact: true }).count(),
        0,
      );
    },
  );
  await check("Theme and keyboard search remain available", async () => {
    await page.getByRole("button", { name: "Use dark theme" }).click();
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
    await page.keyboard.press("Control+k");
    assert.equal(
      await page
        .getByRole("textbox", { name: "Search approved conversation history" })
        .evaluate((e) => e === document.activeElement),
      true,
    );
    await page.getByRole("button", { name: "Use light theme" }).click();
  });
  await check(
    "Live counters grow without collector notifications or full snapshot reloads, and stop off-view",
    async () => {
      const originalPaused = paused;
      paused = false;
      await nav("Overview");
      const stats = page.getByRole("region", { name: "Live index statistics" });
      await stats
        .locator('[data-stat="entries"]')
        .getByText("3", { exact: true })
        .waitFor();
      const snapshots = calls.filter((c) => c.method === "snapshot").length;
      extraStatsEntries = 7;
      await page.waitForFunction(
        () =>
          document.querySelector('[data-stat="entries"]')?.textContent === "10",
      );
      assert.equal(
        (await stats.locator('[data-stat="disk"]').innerText()).trim(),
        "7.00 MB",
      );
      assert.ok((await stats.innerText()).includes("+7 this visit"));
      assert.equal(
        calls.filter((c) => c.method === "snapshot").length,
        snapshots,
      );
      assert.ok((await stats.innerText()).includes("4 files"));
      assert.ok((await stats.innerText()).includes("64.00 MB remaining"));
      await page.locator('[data-sidebar-version="0.4.3"]').waitFor();
      paused = originalPaused;
      await nav("Sessions");
      const stopped = calls.filter(
        (c) => c.method === "collection.stats",
      ).length;
      await page.waitForTimeout(2300);
      assert.equal(
        calls.filter((c) => c.method === "collection.stats").length,
        stopped,
      );
      extraStatsEntries = 0;
    },
  );
  await check(
    "Overview loads local reminders only and distinguishes unavailable from empty",
    async () => {
      await nav("Overview");
      const panel = page.getByRole("region", { name: "Open loops" });
      await panel.getByText("fix/unpushed-work", { exact: true }).waitFor();
      assert.ok(!(await panel.innerText()).includes("GitHub"));
      assert.equal(await panel.locator('[data-kind="pr"]').count(), 0);
      assert.equal(
        await panel.getByRole("group", { name: "On you" }).count(),
        0,
      );
      assert.equal(
        await panel.getByRole("group", { name: "Waiting on others" }).count(),
        0,
      );
      assert.equal(
        await panel.getByRole("group", { name: "Drafts not sent" }).count(),
        0,
      );
      localUnavailable = true;
      await panel.getByRole("button", { name: "Refresh open loops" }).click();
      await panel.getByText("Local reminders could not be loaded.").waitFor();
      assert.ok(
        !(await panel.innerText()).includes("No local branch reminders"),
      );
      localUnavailable = false;
      localEmpty = true;
      await panel.getByRole("button", { name: "Refresh open loops" }).click();
      await panel
        .getByText("No local branch reminders or conversation drafts found.")
        .waitFor();
      localEmpty = false;
      await panel.getByRole("button", { name: "Refresh open loops" }).click();
      await panel.getByText("fix/unpushed-work", { exact: true }).waitFor();
      assert.ok(
        !calls.some((c) => c.method.startsWith("pullRequests")),
        "No GitHub RPC during load or refresh",
      );
    },
  );
  await check("Open loops keeps every local branch reachable", async () => {
    const panel = page.getByRole("region", { name: "Open loops" });
    loopsOverflow = true;
    try {
      await panel.getByRole("button", { name: "Refresh open loops" }).click();
      const branches = panel.getByRole("group", { name: "Not pushed" });
      await branches.getByText("topic/branch-5", { exact: true }).waitFor();
      assert.equal(await branches.getByText("topic/branch-6").count(), 0);
      await branches.getByRole("button", { name: "Show 1 more" }).click();
      await branches.getByText("topic/branch-6", { exact: true }).waitFor();
      const text = await branches.innerText();
      assert.ok(text.includes("Not verified"));
      assert.ok(text.includes("2 commits not pushed"));
      assert.ok(text.includes("2 more not shown"));
      await branches.getByRole("button", { name: "Show fewer" }).click();
      assert.equal(await branches.getByText("topic/branch-6").count(), 0);
    } finally {
      loopsOverflow = false;
      await panel.getByRole("button", { name: "Refresh open loops" }).click();
      await panel.getByText("fix/unpushed-work", { exact: true }).waitFor();
    }
  });
  await check(
    "Search starts with a prompt while Timeline groups messages by original date",
    async () => {
      const before = calls.filter((c) => c.method === "query").length;
      await nav("Search");
      await page
        .getByRole("heading", { name: "Find something you remember" })
        .waitFor();
      await page.waitForTimeout(350);
      assert.equal(calls.filter((c) => c.method === "query").length, before);
      await nav("Timeline");
      await page.locator(".timeline-day").first().waitFor();
      assert.equal(
        await page
          .getByRole("textbox", { name: "Search messages", exact: true })
          .count(),
        0,
      );
      assert.ok(
        calls.some((c) => c.method === "query" && c.args.byTimestamp === true),
      );
      assert.equal(await page.locator(".timeline-event").count(), 3);
    },
  );
  await check(
    "Sessions open bounded chronological user and final-response history",
    async () => {
      await nav("Sessions");
      await page
        .locator(".session-card")
        .filter({ hasText: "Investigate API response" })
        .first()
        .click();
      await page
        .locator(".conversation-message .evidence-body")
        .filter({ hasText: "FINAL RESPONSE FROM SOURCE" })
        .waitFor();
      assert.equal(
        await page.getByText("Tool calls and results", { exact: true }).count(),
        0,
      );
      assert.ok(
        calls.some((c) => c.method === "conversation" && c.args.limit === 50),
      );
      await page
        .getByRole("button", { name: "Close evidence details" })
        .click();
    },
  );
  await check(
    "Closing during a pending note save cannot reopen the drawer",
    async () => {
      await nav("Timeline");
      await page.locator(".record-row").first().click();
      await page
        .getByLabel("Private context note")
        .fill("Delayed save fixture");
      let release, started;
      const begun = new Promise((resolve) => {
        started = resolve;
      });
      correctionBarrier = {
        started,
        wait: new Promise((resolve) => {
          release = resolve;
        }),
      };
      await page
        .getByRole("button", { name: "Save context", exact: true })
        .click();
      await begun;
      await page
        .getByRole("button", { name: "Close evidence details" })
        .click();
      await nav("Projects");
      release();
      await page.waitForFunction(
        () =>
          !document.querySelector('button[aria-label="Pause all collection"]')
            ?.disabled,
      );
      correctionBarrier = undefined;
      assert.equal(await page.getByRole("dialog").count(), 0);
      await page.locator(".project-card").filter({ hasText: "SP" }).click();
      await page.getByText("Acme/billing", { exact: true }).waitFor();
    },
  );
  await check(
    "Search and source/project filters query local history only",
    async () => {
      await nav("Search");
      await page
        .getByRole("textbox", { name: "Search messages", exact: true })
        .fill("schema");
      await page.waitForFunction(() =>
        document
          .querySelector(".list-summary")
          ?.textContent?.includes("1 matching"),
      );
      await page.getByLabel("Filter by source").selectOption("codex");
      await page.waitForTimeout(400);
      assert.deepEqual(
        await page
          .getByLabel("Filter by source")
          .locator("option")
          .allTextContents(),
        ["All sources", "Claude Code", "Codex"],
      );
      assert.equal(
        await page
          .getByLabel("Filter by project")
          .locator('option[value="__unassigned"]')
          .count(),
        0,
      );
    },
  );
  await check(
    "Multi-repository workspaces remain a single approved project",
    async () => {
      await nav("Projects");
      await page.locator(".project-card").filter({ hasText: "SP" }).click();
      await page.getByText("Acme/billing", { exact: true }).waitFor();
      await page
        .getByText("Acme/report-processor", { exact: true })
        .waitFor();
      assert.equal(
        await page
          .getByRole("button", { name: "Summarize project", exact: true })
          .count(),
        0,
      );
    },
  );
  await check(
    "Discovered project candidates require explicit approval",
    async () => {
      await nav("Settings");
      await page
        .getByRole("tab", { name: "Approved projects", exact: true })
        .click();
      const row = page
        .locator(".project-approval-row")
        .filter({ hasText: "Personal candidate" });
      await row.getByRole("button", { name: "Approve project" }).waitFor();
      await page
        .getByRole("button", { name: "Discover projects", exact: true })
        .click();
      assert.equal(projects.find((p) => p.id === "personal").isApproved, false);
      await row.getByRole("button", { name: "Approve project" }).click();
      await page
        .locator(".sidebar .project-link")
        .filter({ hasText: "Personal candidate" })
        .waitFor();
      assert.equal(projects.find((p) => p.id === "personal").isApproved, true);
    },
  );
  await check(
    "Exclusion requires purge confirmation and immediately removes history",
    async () => {
      const row = page
        .locator(".project-approval-row")
        .filter({ hasText: "Personal candidate" });
      await row
        .getByRole("button", { name: "Exclude & purge", exact: true })
        .click();
      await page.getByRole("button", { name: "Cancel", exact: true }).click();
      assert.equal(projects.find((p) => p.id === "personal").isApproved, true);
      await row
        .getByRole("button", { name: "Exclude & purge", exact: true })
        .click();
      await page
        .getByRole("button", { name: "Exclude & purge history", exact: true })
        .click();
      await page.getByRole("dialog").waitFor({ state: "detached" });
      assert.equal(projects.find((p) => p.id === "personal").isApproved, false);
      assert.equal(
        records.some((r) => r.projectId === "personal"),
        false,
      );
      assert.equal(
        await page
          .locator(".sidebar .project-link")
          .filter({ hasText: "Personal candidate" })
          .count(),
        0,
      );
    },
  );
  await check("Saving a new project never grants approval", async () => {
    await page
      .getByRole("button", { name: "Add project", exact: true })
      .click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Name", { exact: true }).fill("Fresh workspace");
    await dialog
      .getByLabel("Root directory", { exact: true })
      .fill("C:\\Work\\Fresh");
    await dialog
      .getByRole("button", { name: "Save project", exact: true })
      .click();
    await dialog.waitFor({ state: "detached" });
    assert.equal(
      projects.find((p) => p.name === "Fresh workspace").isApproved,
      false,
    );
    assert.equal(
      await page
        .locator(".sidebar .project-link")
        .filter({ hasText: "Fresh workspace" })
        .count(),
      0,
    );
  });
  await check(
    "MCP configuration offers explicit host commands without token fields",
    async () => {
      await page.getByRole("tab", { name: "MCP integration" }).click();
      await page
        .getByRole("button", { name: "Copy Claude Code command" })
        .waitFor();
      assert.equal(await page.locator('input[type="password"]').count(), 0);
      assert.ok(
        (await page.locator(".mcp-command pre").allTextContents()).every((v) =>
          v.includes("mcp add mission-control"),
        ),
      );
    },
  );
  await check(
    "Monitoring and privacy settings contain no inference service",
    async () => {
      await page.getByRole("tab", { name: "Monitoring", exact: true }).click();
      assert.equal(await page.getByLabel("GitHub CLI path (optional)").count(), 0);
      await page
        .getByRole("button", { name: "Resume collection", exact: true })
        .last()
        .click();
      assert.equal(paused, false);
      await page.getByRole("tab", { name: "Privacy & storage" }).click();
      assert.ok(
        (await page.locator("body").innerText()).includes(
          "Mission Control contains no model or inference service.",
        ),
      );
    },
  );
  await check("Project queries refresh during continuous collector notifications", async () => {
    await nav("Projects");
    const before = calls.filter(c => c.method === "sessions" && c.args.limit === 1).length;
    await page.evaluate(async () => {
      const timer = setInterval(() => window.syntheticChanged?.(), 100);
      await new Promise(resolve => setTimeout(resolve, 3600));
      clearInterval(timer);
    });
    assert.ok(calls.filter(c => c.method === "sessions" && c.args.limit === 1).length > before);
  });
  await check("Long messages load later pages without repeating the preview", async () => {
    const message = records.find(r => r.id === "question");
    const original = { ...message };
    longBodies.set(message.id, "LONG_MESSAGE_START\n" + "x".repeat(24000) + "\nLONG_MESSAGE_TAIL");
    message.body = "LONG_MESSAGE_START";
    message.nextBodyOffset = 0;
    await nav("Sessions");
    await page.locator(".session-card").filter({ hasText: "Investigate API response" }).first().click();
    const article = page.locator(".conversation-message").filter({ hasText: "LONG_MESSAGE_START" });
    for (let index = 0; index < 3; index++) {
      await article.getByRole("button", { name: "Read more of this message" }).click();
      await page.waitForTimeout(80);
    }
    const text = await article.locator(".evidence-body").innerText();
    assert.ok(text.includes("LONG_MESSAGE_TAIL"));
    assert.equal(text.split("LONG_MESSAGE_START").length - 1, 1);
    await page.getByRole("button", { name: "Close evidence details" }).click();
    Object.assign(message, original); delete message.nextBodyOffset; longBodies.delete(message.id);
  });
  await check("Source notices identify the source, stay dismissed after reopening, and return when the issue changes", async () => {
    sourceGap = "Referenced Codex history: only present messages are retained.";
    await page.evaluate(() => window.syntheticChanged());
    await page.getByRole("button", { name: "Dismiss source notice" }).waitFor();
    assert.ok((await page.locator(".notice.warning").innerText()).includes("Codex local"));
    assert.ok((await page.locator(".notice.warning").innerText()).includes("referenced messages"));
    await page.getByRole("button", { name: "Dismiss source notice" }).click();
    assert.equal(await page.locator(".notice.warning").count(), 0);
    await page.reload();
    await page.getByRole("heading", { name: "Pick up where you left off." }).waitFor();
    assert.equal(await page.locator(".notice.warning").count(), 0);
    sourceGap = "A newly missing source file cannot be read.";
    await page.evaluate(() => window.syntheticChanged());
    await page.getByRole("button", { name: "Dismiss source notice" }).waitFor();
    await page.getByRole("button", { name: "Review sources" }).click();
    assert.ok((await page.locator(".notice.warning").innerText()).includes("Codex local"));
    sourceGap = null;
    await page.evaluate(() => window.syntheticChanged());
    await page.getByRole("button", { name: "Dismiss source notice" }).waitFor({ state: "detached" });
  });
  await check("No obsolete RPC calls or browser runtime errors", async () => {
    assert.deepEqual(errors, []);
    assert.ok(
      !calls.some((c) =>
        /^(ask|ai|saveAi|testAi|remote\.|commitments|linkedTasks)/.test(
          c.method,
        ),
      ),
    );
    assert.ok(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    );
  });
  await page.screenshot({
    path: path.join(output, "memory-settings.png"),
    fullPage: true,
  });
  await writeFile(
    path.join(output, "report.json"),
    JSON.stringify({ checks, errors, callCount: calls.length }, null, 2),
  );
} finally {
  await browser.close();
  await new Promise((r) => server.close(r));
}
