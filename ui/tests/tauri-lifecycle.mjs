import assert from "node:assert/strict";
import path from "node:path";
import { chromium } from "playwright-core";

// The benchmark launches the isolated release process. This helper only drives
// its dashboard over opt-in loopback CDP; it never discovers a user's instance.
const action = process.argv[2] || "ready";
assert.ok(
  ["ready", "hide", "quit"].includes(action),
  "Expected ready, hide, or quit",
);
assert.ok(
  process.env.MC_NATIVE_TEST_DATA_ROOT,
  "An explicit isolated data directory is required",
);
const cdpUrl = process.env.MC_CDP_URL || "http://127.0.0.1:9241";
const endpoint = new URL(cdpUrl);
assert.equal(endpoint.protocol, "http:");
assert.ok(
  ["127.0.0.1", "localhost", "[::1]"].includes(endpoint.hostname),
  "CDP must stay on loopback",
);
const expectedPath = path.resolve(process.env.MC_NATIVE_TEST_DATA_ROOT);
const normalPath = (value) =>
  process.platform === "win32"
    ? path.resolve(value).toLowerCase()
    : path.resolve(value);
const started = Date.now();
let browser;
let page;
let rpc;
try {
  let connectionError;
  while (Date.now() - started < 45_000) {
    try {
      browser = await chromium.connectOverCDP(cdpUrl, { timeout: 1500 });
      page = browser
        .contexts()
        .flatMap((context) => context.pages())
        .find((candidate) =>
          /^(?:https?:\/\/tauri\.localhost|tauri:\/\/localhost)\//.test(
            candidate.url(),
          ),
        );
      if (page) break;
      await browser.close();
      browser = undefined;
    } catch (error) {
      connectionError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  assert.ok(
    page,
    `An isolated Tauri dashboard did not become available: ${connectionError?.message || "page missing"}`,
  );
  await page.waitForFunction(() => !!window.__TAURI__?.core?.invoke);
  rpc = (method, args = {}) =>
    page.evaluate(
      ({ method, args }) =>
        new Promise((resolve, reject) => {
          const timer = setTimeout(
            () => reject(new Error(`RPC timed out: ${method}`)),
            30_000,
          );
          window.__TAURI__.core
            .invoke("workspace_rpc", { method, args })
            .then(resolve, reject)
            .finally(() => clearTimeout(timer));
        }),
      { method, args },
    );
  const settings = await rpc("settings");
  assert.equal(
    normalPath(settings.dataDirectory),
    normalPath(expectedPath),
    "Refusing to control a dashboard outside the explicit test directory",
  );
  const snapshot = await rpc("snapshot");
  assert.equal(snapshot.mode, process.env.MC_EXPECT_MODE || "demo");
  await page.locator(".sidebar .nav-item").first().waitFor();
  if (action !== "ready") {
    const closed = page.waitForEvent("close", { timeout: 25_000 });
    await page
      .evaluate(
        (method) => {
          void window.__TAURI__.core
            .invoke("workspace_rpc", { method, args: {} })
            .catch((error) => {
              document.body.dataset.lifecycleError = String(error);
            });
        },
        action === "hide" ? "host.hide" : "host.quit",
      )
      .catch((error) => {
        if (!page.isClosed()) throw error;
      });
    await closed;
  }
  console.log(
    JSON.stringify({
      action,
      passed: true,
      elapsedMs: Date.now() - started,
      mode: snapshot.mode,
      records: snapshot.totalRecords,
      monitoring: snapshot.monitoring,
      dataDirectory: expectedPath,
    }),
  );
} finally {
  await browser?.close().catch(() => {});
}
