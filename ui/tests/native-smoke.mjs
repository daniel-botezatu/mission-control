import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { chromium } from "playwright-core";

// Attach only to an explicitly isolated demo release. Never probe installed user data.
assert.ok(
  process.env.MC_NATIVE_TEST_DATA_ROOT,
  "Explicit isolated demo data directory required",
);
const cdpUrl = process.env.MC_CDP_URL || "http://127.0.0.1:9239";
const output = path.resolve(
  process.env.MC_UI_ARTIFACTS || "artifacts/memory-native-ui",
);
await mkdir(output, { recursive: true });
const browser = await chromium.connectOverCDP(cdpUrl);
const page = browser
  .contexts()
  .flatMap((c) => c.pages())
  .find((p) =>
    /^(?:https:\/\/mission-control\.local|https?:\/\/tauri\.localhost|tauri:\/\/localhost)\//.test(
      p.url(),
    ),
  );
assert.ok(page, "Isolated native dashboard missing");
page.setDefaultTimeout(15000);
await page.setViewportSize({ width: 1440, height: 1000 });
const report = {
  started: new Date().toISOString(),
  checks: [],
  pageErrors: [],
  screenshots: [],
};
page.on("pageerror", (e) => report.pageErrors.push(e.message));
const rpc = (method, args = {}) =>
  page.evaluate(
    ({ method, args }) => {
      if (window.__TAURI__?.core?.invoke)
        return window.__TAURI__.core.invoke("workspace_rpc", { method, args });
      return new Promise((resolve, reject) => {
        const id = `memory-smoke-${Date.now()}-${Math.random()}`;
        const bridge = window.chrome?.webview;
        if (!bridge) return reject(new Error("Missing native bridge"));
        const timer = setTimeout(() => {
          bridge.removeEventListener("message", receive);
          reject(new Error(`RPC timeout: ${method}`));
        }, 30000);
        const receive = (e) => {
          let v = e.data;
          if (typeof v === "string") {
            try {
              v = JSON.parse(v);
            } catch {
              return;
            }
          }
          if (String(v?.id) !== id) return;
          clearTimeout(timer);
          bridge.removeEventListener("message", receive);
          if (v.error)
            reject(
              new Error(
                typeof v.error === "string" ? v.error : v.error.message,
              ),
            );
          else resolve(v.result);
        };
        bridge.addEventListener("message", receive);
        bridge.postMessage({ id, method, args });
      });
    },
    { method, args },
  );
const check = async (name, fn) => {
  try {
    const detail = await fn();
    report.checks.push({ name, passed: true, ...(detail ? { detail } : {}) });
    console.log("PASS " + name);
  } catch (e) {
    report.checks.push({ name, passed: false, error: e.stack });
    throw e;
  }
};
const nav = (name) =>
  page
    .locator(".sidebar .nav-item")
    .filter({ hasText: new RegExp(`^${name}$`) })
    .click();
const poll = async (fn, test) => {
  for (let i = 0; i < 100; i++) {
    const v = await fn();
    if (test(v)) return v;
    await new Promise((r) => setTimeout(r, 100));
  }
  assert.fail("Expected local state did not become available");
};
const screenshot = async (name) => {
  const file = path.join(output, name);
  await page.screenshot({ path: file, fullPage: false });
  report.screenshots.push(file);
};
let snapshot, originalTheme, originalPause, correctionId;
try {
  await check(
    "Native bridge uses isolated approved-only local demo",
    async () => {
      const settings = await rpc("settings");
      assert.equal(
        path.resolve(settings.dataDirectory).toLowerCase(),
        path.resolve(process.env.MC_NATIVE_TEST_DATA_ROOT).toLowerCase(),
      );
      snapshot = await rpc("snapshot");
      assert.equal(snapshot.mode, "demo");
      assert.ok(snapshot.projects.length > 0);
      assert.ok(snapshot.projects.every((p) => p.isApproved));
      assert.ok(
        snapshot.records.every(
          (r) =>
            ["claude", "codex"].includes(r.sourceKind) &&
            snapshot.projects.some((p) => p.id === r.projectId && p.isApproved),
        ),
      );
      originalPause = snapshot.monitoring === "paused";
      originalTheme = await page.locator("html").getAttribute("data-theme");
      return {
        projects: snapshot.projects.length,
        records: snapshot.totalRecords,
      };
    },
  );
  await check(
    "Approved overview retains themes and has no built-in chat",
    async () => {
      await nav("Overview");
      await page
        .getByRole("heading", { name: "Pick up where you left off." })
        .waitFor();
      assert.equal(await page.locator(".ask-trigger,.ask-panel").count(), 0);
      if ((await page.locator("html").getAttribute("data-theme")) !== "light")
        await page.getByRole("button", { name: "Use light theme" }).click();
      await screenshot("memory-overview-light.png");
      await page.getByRole("button", { name: "Use dark theme" }).click();
      await screenshot("memory-overview-dark.png");
      assert.ok(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      );
      await page.getByRole("button", { name: "Use light theme" }).click();
    },
  );
  await check(
    "Sessions expose bounded source conversation without tool-output panel",
    async () => {
      await nav("Sessions");
      await page.locator(".session-card").first().waitFor();
      await page.locator(".session-card").first().click();
      await page.locator(".conversation-message").first().waitFor();
      assert.equal(
        await page.getByText("Tool calls and results", { exact: true }).count(),
        0,
      );
      await screenshot("memory-conversation.png");
      await page
        .getByRole("button", { name: "Close evidence details" })
        .click();
    },
  );
  await check(
    "Keyboard history search and local source filters work",
    async () => {
      await page.keyboard.press("Control+k");
      const search = page.getByRole("textbox", {
        name: "Search approved conversation history",
      });
      assert.ok(await search.evaluate((e) => e === document.activeElement));
      await search.fill("migration");
      await page
        .getByRole("heading", { name: "Search conversation history" })
        .waitFor();
      await poll(
        () => page.locator(".list-summary").innerText(),
        (v) => !v.includes("Finding"),
      );
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
      await screenshot("memory-search.png");
    },
  );
  await check("Record notes remain separate and editable", async () => {
    await nav("Timeline");
    await page.locator(".record-row").first().waitFor();
    await page.locator(".record-row").first().click();
    const recordId = await page.locator(".record-metadata").innerText();
    const record = snapshot.records.find((r) => recordId.includes(r.id));
    assert.ok(record);
    correctionId = record.id;
    await page.getByLabel("Private context note").fill("NATIVE MEMORY QA NOTE");
    await page
      .getByRole("button", { name: "Save context", exact: true })
      .click();
    await poll(
      () => rpc("recordCorrection", { id: correctionId }),
      (v) => v?.note === "NATIVE MEMORY QA NOTE",
    );
    await page
      .getByRole("button", { name: "Undo correction", exact: true })
      .click();
    await poll(
      () => rpc("recordCorrection", { id: correctionId }),
      (v) => !v,
    );
    correctionId = null;
    await page.getByRole("button", { name: "Close evidence details" }).click();
  });
  await check(
    "Projects show local repository context and source history",
    async () => {
      await nav("Projects");
      await page.locator(".project-card").first().click();
      await page
        .getByText("APPROVED LOCAL DIRECTORY", { exact: true })
        .waitFor();
      assert.equal(
        await page
          .getByRole("button", { name: "Summarize project", exact: true })
          .count(),
        0,
      );
      await screenshot("memory-project.png");
    },
  );
  await check(
    "Approved project settings expose explicit exclusion confirmation",
    async () => {
      await nav("Settings");
      await page
        .getByRole("tab", { name: "Approved projects", exact: true })
        .click();
      await page.locator(".project-approval-row").first().waitFor();
      await page
        .getByRole("button", { name: "Exclude & purge", exact: true })
        .first()
        .click();
      await page.getByRole("heading", { name: /and purge history/ }).waitFor();
      await screenshot("memory-exclude-confirmation.png");
      await page.getByRole("button", { name: "Cancel", exact: true }).click();
      assert.equal(await page.getByRole("dialog").count(), 0);
    },
  );
  await check(
    "MCP settings contain exact host setup commands and no credentials",
    async () => {
      await page.getByRole("tab", { name: "MCP integration" }).click();
      await page
        .getByRole("button", { name: "Copy Claude Code command" })
        .waitFor();
      assert.equal(await page.locator('input[type="password"]').count(), 0);
      assert.equal(await page.locator(".mcp-command pre").count(), 2);
      const config = await rpc("mcpConfig");
      assert.ok(config.executablePath.endsWith("mission-control-mcp.exe"));
      assert.ok(config.claudeCommand.includes("mcp add"));
      assert.ok(config.codexCommand.includes("mcp add"));
      await screenshot("memory-mcp-settings.png");
    },
  );
  await check(
    "Pause and resume remain responsive without AI services",
    async () => {
      await rpc("control", { action: "pause" });
      await poll(
        () => rpc("snapshot"),
        (v) => v.monitoring === "paused",
      );
      await rpc("control", { action: "resume" });
      await poll(
        () => rpc("snapshot"),
        (v) => v.monitoring !== "paused",
      );
      assert.deepEqual(report.pageErrors, []);
    },
  );
  await check(
    "Live counters show full totals, grow during collection, and report the running version",
    async () => {
      await nav("Overview");
      const stats = await rpc("collection.stats");
      assert.equal(stats.mode, "demo");
      assert.equal(stats.savedEntries, (await rpc("snapshot")).totalRecords);
      assert.ok(
        stats.databaseBytes >= stats.activeDatabaseBytes &&
          stats.activeDatabaseBytes > 0,
      );
      const version = await rpc("host.version");
      assert.equal(version.version, "0.4.17");
      await page.locator('[data-sidebar-version="0.4.17"]').waitFor();
      await page.waitForFunction(
        (count) =>
          document.querySelector('[data-stat="entries"]')?.textContent ===
          count.toLocaleString(),
        stats.savedEntries,
      );
      await rpc("demoReplay");
      const changed = await poll(
        () => rpc("collection.stats"),
        (value) => value.savedEntries > stats.savedEntries,
      );
      await page.waitForFunction(
        (count) =>
          document.querySelector('[data-stat="entries"]')?.textContent ===
          count.toLocaleString(),
        changed.savedEntries,
      );
      await screenshot("live-counters-light.png");
      await page.getByRole("button", { name: "Use dark theme" }).click();
      await screenshot("live-counters-dark.png");
      await page.getByRole("button", { name: "Use light theme" }).click();
      return {
        before: stats.savedEntries,
        after: changed.savedEntries,
        version: version.version,
      };
    },
  );
} finally {
  if (correctionId)
    await rpc("undoCorrection", { recordId: correctionId }).catch(() => {});
  if (originalPause) await rpc("control", { action: "pause" }).catch(() => {});
  if (
    originalTheme &&
    (await page.locator("html").getAttribute("data-theme")) !== originalTheme
  )
    await page
      .getByRole("button", { name: `Use ${originalTheme} theme` })
      .click()
      .catch(() => {});
  await writeFile(
    path.join(output, "report.json"),
    JSON.stringify(report, null, 2),
  );
  await browser.close();
}
