import assert from "node:assert/strict";
import { chromium } from "playwright-core";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

const root = path.resolve("artifacts/recap-fixes");
const pid = Number(readFileSync(path.join(root, "native-desktop-pid.txt"), "utf8").trim());
assert.ok(Number.isInteger(pid) && pid > 0);
const browser = await chromium.connectOverCDP("http://127.0.0.1:9246");
try {
  const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url().includes("tauri.localhost"));
  assert.ok(page);
  const settings = await page.evaluate(() => window.__TAURI__.core.invoke("workspace_rpc", { method: "settings", args: {} }));
  assert.equal(path.resolve(settings.dataDirectory).toLowerCase(), path.join(root, "native-data").toLowerCase());
  // Observe the actual native RPC calls; no mock statistics or fake visibility.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Debugger.enable");
  const fn = await cdp.send("Runtime.evaluate", { expression: "window.__TAURI__.core.invoke" });
  let calls = 0;
  cdp.on("Debugger.paused", async () => { calls++; await cdp.send("Debugger.resume"); });
  await cdp.send("Debugger.setBreakpointOnFunctionCall", {
    objectId: fn.result.objectId,
    condition: "arguments[0] === 'workspace_rpc' && ['collection.stats','snapshot'].includes(arguments[1]?.method)",
  });
  const changeWindow = (state) => execFileSync("powershell.exe", ["-NoProfile", "-Command",
    `Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class VisibilityProbe { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int state); }'; $p=Get-Process -Id ${pid}; if ($p.MainWindowHandle -eq 0) { throw 'Test window missing' }; [VisibilityProbe]::ShowWindow($p.MainWindowHandle, ${state}) | Out-Null`
  ], { windowsHide: true, encoding: "utf8" });
  await new Promise(r => setTimeout(r, 3000));
  assert.ok(calls > 0, "Visible counters must refresh");
  changeWindow(6);
  await page.waitForFunction(() => window.__MC_MINIMIZED__ === true, null, { polling: 100 });
  await new Promise(r => setTimeout(r, 800));
  calls = 0;
  await new Promise(r => setTimeout(r, 4500));
  const minimizedCalls = calls;
  assert.equal(minimizedCalls, 0, "Minimized dashboard must not query statistics/snapshots");
  changeWindow(9);
  await page.waitForFunction(() => window.__MC_MINIMIZED__ === false, null, { polling: 100 });
  await new Promise(r => setTimeout(r, 2500));
  assert.ok(calls > 0, "Counters resume on restore");
  const report = { passed: true, minimizedCalls, restoredCalls: calls };
  await cdp.send("Debugger.disable");
  writeFileSync(path.join(root, "native-visibility.json"), JSON.stringify(report));
  console.log(JSON.stringify(report));
} finally { await browser.close(); }
