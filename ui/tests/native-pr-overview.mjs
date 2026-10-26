import assert from "node:assert/strict";
import path from "node:path";
import { mkdir, writeFile } from "node:fs/promises";
import { chromium } from "playwright-core";

// Explicit opt-in real-account READ on an isolated desktop workspace. No PR titles,
// repositories or account names enter the report; no browser links are opened.
assert.equal(
  process.env.MC_TEST_GITHUB_READ,
  "1",
  "Opt in to the read-only GitHub query",
);
assert.ok(
  process.env.MC_NATIVE_TEST_DATA_ROOT,
  "Explicit isolated workspace required",
);
const output = path.resolve(
  process.env.MC_UI_ARTIFACTS || "artifacts/overview-native-ui",
);
await mkdir(output, { recursive: true });
const browser = await chromium.connectOverCDP(
  process.env.MC_CDP_URL || "http://127.0.0.1:9239",
);
try {
  const page = browser
    .contexts()
    .flatMap((c) => c.pages())
    .find((p) => /^https?:\/\/tauri\.localhost\//.test(p.url()));
  assert.ok(page);
  const rpc = (method, args = {}) =>
    page.evaluate(
      ({ method, args }) =>
        window.__TAURI__.core.invoke("workspace_rpc", { method, args }),
      { method, args },
    );
  const settings = await rpc("settings");
  assert.equal(
    path.resolve(settings.dataDirectory).toLowerCase(),
    path.resolve(process.env.MC_NATIVE_TEST_DATA_ROOT).toLowerCase(),
  );
  assert.equal((await rpc("snapshot")).mode, "demo");
  assert.equal((await rpc("pullRequests")).state, "demo");
  await rpc("mode", { mode: "live" });
  try {
    const data = await rpc("pullRequests");
    assert.equal(
      data.state,
      "ready",
      data.error || "GitHub overview unavailable",
    );
    assert.ok(data.login && data.total >= data.items.length);
    assert.ok(
      data.items.every(
        (pr) =>
          pr.url === `https://github.com/${pr.repository}/pull/${pr.number}`,
      ),
    );
    await page
      .locator(".sidebar .nav-item")
      .filter({ hasText: /^Overview$/ })
      .click();
    const panel = page.getByRole("region", { name: "Your open pull requests" });
    await panel
      .getByText(`Authored by ${data.login} · all repositories`, {
        exact: true,
      })
      .waitFor();
    assert.equal(
      await panel.locator(".pull-request-row").count(),
      Math.min(6, data.items.length),
    );
    const started = Date.now();
    await rpc("control", { action: "refresh" });
    const refreshMs = Date.now() - started;
    assert.ok(refreshMs < 3000, "Refresh must acknowledge scheduling promptly");
    await assert.rejects(
      () => rpc("pullRequests.open", { url: "https://example.com/untrusted" }),
      /loaded overview/,
    );
    const report = {
      passed: true,
      githubCliReadVerified: true,
      total: data.total,
      shown: Math.min(6, data.items.length),
      refreshMs,
      demoMakesNoRequest: true,
      untrustedLinkRejected: true,
    };
    await writeFile(
      path.join(output, "github-overview.json"),
      JSON.stringify(report, null, 2),
    );
    console.log(JSON.stringify(report));
  } finally {
    await rpc("mode", { mode: "demo" });
  }
} finally {
  await browser.close();
}
