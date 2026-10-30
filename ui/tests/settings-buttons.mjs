// Real-app check of the Settings buttons on Windows: drives an isolated desktop instance (demo data,
// explicit throwaway data folder) over loopback CDP and verifies each button's actual effect.
//
//   set MC_NATIVE_TEST_DATA_ROOT=C:\path\to\new-empty-folder
//   set MC_DESKTOP_EXE=...\mission-control.exe   (optional; defaults to the installed app)
//   node ui/tests/settings-buttons.mjs
//
// It never touches the user's own data folder. Start at sign-in is toggled on and off again; an
// existing startup entry that belongs to another data folder makes the test stop before changing it.
import { chromium } from "playwright-core";
import { spawn, execSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import path from "node:path";

if (process.platform !== "win32") { console.log("settings-buttons: Windows only; skipped."); process.exit(0); }
const data = process.env.MC_NATIVE_TEST_DATA_ROOT;
if (!data) { console.error("Set MC_NATIVE_TEST_DATA_ROOT to a new, empty test folder."); process.exit(2); }
fs.mkdirSync(data, { recursive: true });
if (fs.readdirSync(data).length) { console.error("MC_NATIVE_TEST_DATA_ROOT must be empty so the test cannot reuse real data."); process.exit(2); }
const exe = process.env.MC_DESKTOP_EXE || path.join(process.env.LOCALAPPDATA, "Mission Control", "mission-control.exe");
const port = Number(process.env.MC_CDP_PORT || 9263);
const closeHelper = fileURLToPath(new URL("./helpers/close-dashboard-window.ps1", import.meta.url));
const marker = path.resolve(data).toLowerCase(); // full path, so cleanup can only match this test's processes

const results = [];
const record = (name, ok, detail = "") => { results.push({ name, ok, detail }); console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  — " + detail : ""}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const runKey = () => { try { return execSync('reg query "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run" /v MissionControl', { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }); } catch { return null; } };
const ours = () => execSync(`powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.ToLower().Contains('${marker.replace(/'/g, "''")}') } | ForEach-Object { '{0}|{1}' -f $_.ProcessId, $_.Name }"`, { encoding: "utf8" }).split(/\r?\n/).filter(Boolean).map(line => { const [pid, name] = line.split("|"); return { pid: Number(pid), name }; });
const running = () => ours().map(p => p.name).filter(n => n.startsWith("mission-control"));
const closeDashboardWindow = pid => Number(execSync(`powershell -NoProfile -ExecutionPolicy Bypass -File "${closeHelper}" -ProcessId ${pid}`, { encoding: "utf8" }).trim() || 0);
const launch = () => spawn(exe, ["--demo", "--data-dir", data, "--automation-port", String(port)], { stdio: "ignore" });

async function dashboard(timeout = 45000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    try {
      const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout: 1500 });
      const page = browser.contexts().flatMap(c => c.pages()).find(p => /tauri\.localhost|tauri:\/\/localhost/.test(p.url()));
      if (page) { await page.waitForFunction(() => !!window.__TAURI__?.core?.invoke); return { browser, page }; }
      await browser.close();
    } catch { /* not ready yet */ }
    await sleep(300);
  }
  throw new Error("The isolated dashboard did not appear.");
}
const rpc = (page, method, args = {}) => page.evaluate(({ method, args }) => window.__TAURI__.core.invoke("workspace_rpc", { method, args }), { method, args });
const visibleError = page => page.evaluate(() => [...document.querySelectorAll('[role="alert"], .error, .toast.error')].map(e => e.textContent.trim()).filter(Boolean).join(" | "));
const toast = page => page.evaluate(() => [...document.querySelectorAll('[role="status"], .toast')].map(e => e.textContent.trim()).filter(Boolean).join(" | "));

const existing = runKey();
if (existing && !existing.toLowerCase().includes(marker)) { console.error("A Mission Control startup entry already exists for another data folder; not changing it."); process.exit(2); }

let shell = launch();
let browser;
try {
  let page;
  ({ browser, page } = await dashboard());
  const settings0 = await rpc(page, "settings");
  if (path.resolve(settings0.dataDirectory).toLowerCase() !== path.resolve(data).toLowerCase()) throw new Error("Refusing to drive a dashboard outside the test folder.");
  record("isolated instance uses the test data folder", true, settings0.dataDirectory);
  await page.getByRole("button", { name: "Settings" }).click();

  // Monitoring tab: collection settings and lifecycle
  await page.getByRole("tab", { name: "Monitoring" }).click();
  await page.getByLabel("History lookback (days)").fill("45");
  await page.getByLabel("Workspace storage budget (MB)").fill("2048");
  await page.getByRole("button", { name: "Save collection settings" }).click();
  await sleep(1500);
  const s1 = await rpc(page, "settings");
  record("Save collection settings stores lookback and budget", s1.lookbackDays === 45 && s1.storageLimitMb === 2048, `lookback=${s1.lookbackDays} budget=${s1.storageLimitMb}`);
  record("Desktop has no GitHub CLI setting", await page.getByLabel("GitHub CLI path (optional)").count() === 0 && !("githubCliPath" in s1));
  const removed = [];
  for (const method of ["pullRequests", "pullRequests.open", "openExternal", "legacy.cleanup"]) {
    try { await rpc(page, method, { refresh: true, url: "https://github.com/example/project/pull/1" }); }
    catch (error) { if (/Unsupported local memory operation/.test(String(error))) removed.push(method); }
  }
  record("Removed provider, browser and credential RPCs are unavailable", removed.length === 4);

  await page.getByRole("button", { name: "Pause all", exact: true }).click();
  await sleep(1500);
  const paused = await rpc(page, "snapshot");
  record("Pause all stops monitoring", /pause/i.test(String(paused.monitoring)) && await page.getByRole("button", { name: "Resume collection", exact: true }).first().isVisible(), `monitoring=${paused.monitoring}`);
  await page.getByRole("button", { name: "Resume collection", exact: true }).first().click();
  await sleep(2500);
  const resumed = await rpc(page, "snapshot");
  record("Resume collection restarts monitoring", !/pause/i.test(String(resumed.monitoring)), `monitoring=${resumed.monitoring}`);

  // Privacy & storage tab: purge, sign-in, retention
  await page.getByRole("tab", { name: "Privacy & storage" }).click();
  record("Legacy credential cleanup controls are absent", !("retiredCredentials" in s1) && await page.getByRole("button", { name: "Retry credential cleanup" }).count() === 0);
  const before = (await rpc(page, "snapshot")).totalRecords;
  await page.getByRole("button", { name: "Purge excluded history" }).click();
  await sleep(2000);
  const purgeErr = await visibleError(page);
  record("Purge excluded history runs without error", !purgeErr, `${purgeErr || (await toast(page))}`.slice(0, 120));

  // The checkbox follows the saved state, so wait for the save instead of expecting an instant change.
  const box = page.getByLabel("Start Mission Control at sign-in");
  await box.click();
  await sleep(2000);
  record("Sign-in checkbox shows as ticked after saving", await box.isChecked(), await visibleError(page));
  const key = runKey();
  record("Start at sign-in adds the Windows startup entry", !!key && key.includes("--background") && key.toLowerCase().includes(marker) && (await rpc(page, "settings")).startWithWindows === true, (key || "no entry").trim().split(/\r?\n/).pop().trim().slice(0, 160));
  await box.click();
  await sleep(2000);
  record("Turning it off removes the startup entry", runKey() === null && (await rpc(page, "settings")).startWithWindows === false);

  await page.getByLabel("Retain conversation history for").selectOption("30");
  await page.getByRole("button", { name: "Apply retention" }).click();
  await sleep(2000);
  const retErr = await visibleError(page);
  const savedDays = (await rpc(page, "settings")).retentionDays;
  record("Apply retention runs and saves the chosen period", !retErr && savedDays === 30, `days=${savedDays}; records ${before} → ${(await rpc(page, "snapshot")).totalRecords}`);

  // Lifecycle: close to tray, reopen, window X button, quit
  await page.getByRole("tab", { name: "Monitoring" }).click();
  const closed = page.waitForEvent("close", { timeout: 20000 });
  await page.getByRole("button", { name: "Close dashboard to tray" }).click();
  await closed;
  await sleep(2000);
  let alive = running();
  record("Close dashboard to tray keeps collector and tray running", shell.exitCode === null && alive.includes("mission-control.exe") && alive.includes("mission-control-backend.exe"), alive.join(", "));
  await browser.close().catch(() => { });

  launch(); // a second launch asks the running instance to show its dashboard
  let reopened = await dashboard(20000).catch(() => undefined);
  record("Opening again shows the dashboard from the tray instance", !!reopened);
  if (reopened) {
    const xClosed = reopened.page.waitForEvent("close", { timeout: 20000 }).then(() => true, () => false);
    const sent = closeDashboardWindow(shell.pid);
    const closedByX = await xClosed;
    await sleep(4000);
    alive = running();
    record("Window X button keeps collector and tray running", sent === 1 && closedByX && shell.exitCode === null && alive.includes("mission-control-backend.exe"), `running=${alive.join(", ")}`);
    await reopened.browser.close().catch(() => { });
    launch();
    reopened = await dashboard(20000).catch(() => undefined);
    record("Dashboard reopens after the X button", !!reopened);
  }
  if (reopened) {
    browser = reopened.browser;
    await reopened.page.getByRole("button", { name: "Settings" }).click();
    await reopened.page.getByRole("tab", { name: "Monitoring" }).click();
    const exited = new Promise(resolve => shell.once("exit", resolve));
    await reopened.page.getByRole("button", { name: "Quit completely" }).click().catch(() => { });
    await Promise.race([exited, sleep(30000)]);
    await sleep(1500);
    const left = running();
    const wal = path.join(data, "demo", "workspace.db-wal");
    const pending = fs.existsSync(wal) && fs.statSync(wal).size > 0;
    record("Quit completely exits every process and saves the database", shell.exitCode !== null && left.length === 0 && !pending, `exit=${shell.exitCode} remaining=[${left.join(", ")}] walPending=${pending}`);
  }
} catch (error) {
  record("settings-buttons", false, error.message);
} finally {
  await browser?.close().catch(() => { });
  const key = runKey();
  if (key && key.toLowerCase().includes(marker)) execSync('reg delete "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run" /v MissionControl /f', { stdio: "ignore" });
  // A forced stop can leave this instance's WebView holding the debugging port; end only this test's processes.
  for (const child of ours()) { try { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: "ignore" }); } catch { /* already gone */ } }
  const failed = results.filter(r => !r.ok).length;
  console.log(JSON.stringify({ passed: results.length - failed, failed }));
  process.exitCode = failed ? 1 : 0;
}
