import assert from "node:assert/strict";
import path from "node:path";
import { chromium } from "playwright-core";

// Read-only health check for an explicitly named installed workspace. It never
// logs evidence, account names, resource names, or credential material.
const [dataDirectory, action = "health"] = process.argv.slice(2);
assert.ok(dataDirectory && path.isAbsolute(dataDirectory));
assert.ok(["health", "quit"].includes(action));
const browser = await chromium.connectOverCDP("http://127.0.0.1:9245");
try {
  const page = browser
    .contexts()
    .flatMap((context) => context.pages())
    .find((p) => /^https?:\/\/tauri\.localhost\//.test(p.url()));
  assert.ok(page, "Installed Tauri dashboard is missing");
  await page.waitForFunction(() => !!window.__TAURI__?.core?.invoke);
  const call = (method, args = {}) =>
    page.evaluate(
      ({ method, args }) =>
        window.__TAURI__.core.invoke("workspace_rpc", { method, args }),
      { method, args },
    );
  const settings = await call("settings");
  assert.equal(
    path.resolve(settings.dataDirectory).toLowerCase(),
    path.resolve(dataDirectory).toLowerCase(),
  );
  const snapshot = await call("snapshot");
  await page.locator(".sidebar .nav-item").first().waitFor();
  console.log(
    JSON.stringify({
      healthy: true,
      mode: snapshot.mode,
      monitoring: snapshot.monitoring,
      records: snapshot.totalRecords,
      projects: snapshot.projects.length,
      sources: snapshot.sources.map((s) => ({
        kind: s.kind,
        enabled: s.enabled,
        state: s.state,
        pending: s.pending,
        hasGap: !!s.gap,
      })),
      dataDirectoryMatched: true,
      startupEnabled: settings.startWithWindows,
    }),
  );
  if (action === "quit") {
    const closed = page.waitForEvent("close");
    await page
      .evaluate(() => {
        void window.__TAURI__.core
          .invoke("workspace_rpc", { method: "host.quit", args: {} })
          .catch(() => {});
      })
      .catch((error) => {
        if (!page.isClosed()) throw error;
      });
    await closed;
  }
} finally {
  await browser.close().catch(() => {});
}
