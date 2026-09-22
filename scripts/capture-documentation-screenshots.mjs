// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, writeFile, realpath } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
assert.equal(process.argv.length, 3, "Pass the manifest created by the screenshot host.");
const manifestPath = path.resolve(process.argv[2]);
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
assert.equal(manifest.schemaVersion, 1);
assert.match(manifest.runId, /^[a-f0-9]{32}$/);
const runDirectory = path.join(workspace, "artifacts", "documentation-screenshots", "runs", manifest.runId);
assert.equal(await realpath(path.dirname(manifestPath)), await realpath(runDirectory));
assert.equal(manifest.databasePath, path.join(runDirectory, "screenshots.db"));
assert.equal(manifest.imagesDirectory, path.join(runDirectory, "images"));
const owner = JSON.parse(await readFile(path.join(runDirectory, "owner.json"), "utf8"));
assert.equal(owner.purpose, "ksr-documentation-screenshots");
assert.equal(owner.runId, manifest.runId);
const baseUrl = new URL(manifest.baseUrl);
assert.equal(baseUrl.protocol, "http:");
assert.equal(baseUrl.hostname, "127.0.0.1");
assert.ok(Number(baseUrl.port) >= 1024 && baseUrl.port !== "5057", "Never capture the live app.");
assert.equal(baseUrl.href, `${baseUrl.origin}/`);
assert.equal(manifest.activityIds.length, 10);
assert.ok(manifest.activityIds.every(name => /^Demo\.[A-Za-z0-9]+$/.test(name)));
const age = Date.now() - new Date(manifest.nowUtc).getTime();
assert.ok(age >= 0 && age < 15 * 60 * 1000, "Create a fresh fixture before capturing; do not reuse aging synthetic leases.");

async function verifyFixture() {
  const response = await fetch(new URL("/api/v1/system/status", baseUrl), { signal: AbortSignal.timeout(10000) });
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("x-ksr-screenshot-fixture"), manifest.runId);
  const status = await response.json();
  assert.equal(status.database.path, manifest.databasePath);
  assert.equal(status.database.jobCount, manifest.activityIds.length);
  for (const component of ["scheduler", "workerPool", "retention", "update"]) {
    assert.equal(status[component].enabled, false, `${component} must be disabled.`);
  }
  assert.equal(status.workerPool.starts, 0);
}

await verifyFixture();
const browser = await chromium.launch({ headless: true });
const errors = [];
const captured = [];
try {
  const context = await browser.newContext({
    viewport: { width: 1600, height: 1000 },
    deviceScaleFactor: 1.5,
    locale: "en-US",
    timezoneId: "UTC",
    colorScheme: "light",
    reducedMotion: "reduce",
    serviceWorkers: "block"
  });
  await context.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const allowedWrite = request.method() === "POST" &&
      (url.pathname === "/api/v1/dependency-graphs/kusto-lineage" ||
       /^\/ui-api\/v1\/jobs\/[a-f0-9-]+\/failure-analyses$/.test(url.pathname));
    if (url.origin !== baseUrl.origin || (!["GET", "HEAD"].includes(request.method()) && !allowedWrite)) {
      errors.push(`Forbidden browser request: ${request.method()} ${url.origin}${url.pathname}`);
      await route.abort("blockedbyclient");
      return;
    }
    await route.continue();
  });
  const page = await context.newPage();
  await page.clock.setFixedTime(new Date(manifest.nowUtc));
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  page.on("response", response => {
    if (response.status() >= 400) errors.push(`HTTP ${response.status()}: ${response.url()}`);
  });
  page.on("popup", popup => {
    errors.push("Unexpected popup.");
    void popup.close();
  });

  async function ready() {
    await page.evaluate(async () => {
      await document.fonts.ready;
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    await page.waitForFunction(() => [...document.querySelectorAll("canvas")]
      .filter(canvas => !canvas.closest("[data-dependency-graph]") && canvas.getBoundingClientRect().height > 0)
      .every(canvas => window.Chart?.getChart(canvas)?.width > 0));
    const evidence = await page.evaluate(() => ({
      text: document.body.innerText,
      links: [...document.querySelectorAll("a[href]")].filter(a => a.getClientRects().length).map(a => a.href)
    }));
    assert.doesNotMatch(evidence.text, /[A-Z]:\\Users\\|\.kusto\.windows\.net|@microsoft\.com|Bearer\s+[A-Za-z0-9]/i);
    for (const href of evidence.links) {
      const url = new URL(href);
      if (url.origin !== baseUrl.origin) {
        assert.equal(url.hostname, "dataexplorer.azure.com", "Only synthetic ADX deep links may appear.");
        assert.ok(decodeURIComponent(href).includes("ksr-example.invalid"), "ADX links must target the reserved fixture host.");
      }
    }
    assert.deepEqual(errors, [], "Capture must have no page, network, or console errors.");
  }

  async function navigate(route) {
    const response = await page.goto(new URL(route, baseUrl).href, { waitUntil: "networkidle" });
    assert.equal(response.status(), 200);
    assert.equal(response.headers()["x-ksr-screenshot-fixture"], manifest.runId);
    await page.mouse.move(0, 0);
    await ready();
  }

  async function record(name, screenshot) {
    const png = await screenshot;
    assert.deepEqual([...png.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
    const width = png.readUInt32BE(16);
    const height = png.readUInt32BE(20);
    assert.ok(width >= 2000 && height >= 500, `${name}: unexpected image dimensions.`);
    for (let offset = 8; offset < png.length;) {
      const size = png.readUInt32BE(offset);
      const type = png.toString("ascii", offset + 4, offset + 8);
      assert.ok(["IHDR", "IDAT", "IEND", "sRGB", "gAMA", "cHRM", "pHYs"].includes(type), `Unexpected PNG metadata: ${type}`);
      offset += size + 12;
    }
    await writeFile(path.join(manifest.imagesDirectory, name), png, { flag: "wx" });
    captured.push({ name, width, height, viewport: page.viewportSize(), sha256: createHash("sha256").update(png).digest("hex") });
    console.log(`Captured ${name} (${width} x ${height})`);
  }

  await navigate("/");
  const jobText = await page.locator(".jobs-main").innerText();
  for (const name of manifest.activityIds) assert.ok(jobText.includes(name));
  const overviewBox = await page.locator(".jobs-main > .card").first().boundingBox();
  const overviewHeight = Math.ceil(overviewBox.y + overviewBox.height + 16);
  await record("job-overview.png", page.screenshot({
    clip: { x: 0, y: 0, width: 1600, height: overviewHeight },
    fullPage: true, animations: "disabled", caret: "hide", scale: "device"
  }));

  await navigate(`/jobs/${manifest.detailJobId}?range=1d`);
  assert.ok(await page.locator(".slice-history-grid .completed-after-retry").count() > 0);
  assert.ok(await page.locator(".slice-history-grid .running").count() > 0);
  const detailBox = await page.locator("main").boundingBox();
  const resultsBox = await page.locator(".job-execution-charts .chart-grid > div").first().boundingBox();
  await record("job-detail.png", page.screenshot({
    clip: { x: detailBox.x, y: detailBox.y, width: detailBox.width, height: Math.ceil(resultsBox.y + resultsBox.height - detailBox.y + 12) },
    fullPage: true, animations: "disabled", caret: "hide", scale: "device"
  }));

  await page.setViewportSize({ width: 1600, height: 600 });
  await page.getByRole("tab", { name: "Dependencies", exact: true }).click();
  await page.getByRole("button", { name: "Resolve Kusto lineage" }).click();
  await page.locator("[data-dependency-graph-status]").filter({ hasText: "Resolved 9 Kusto entity(ies) and 1 implicit dependency(ies)." }).waitFor();
  await page.waitForFunction(() => {
    const cy = document.querySelector("[data-dependency-graph]")?.__depGraphCy;
    return cy && cy.nodes().length === 16 && cy.nodes().every(node => Number.isFinite(node.renderedPosition().x));
  });
  await page.mouse.move(0, 0);
  await ready();
  await record("dependency-graph-lineage.png", page.locator(".job-tabs").screenshot({ animations: "disabled", caret: "hide", scale: "device" }));

  await page.setViewportSize({ width: 1600, height: 1000 });
  await navigate("/activity?range=1d");
  for (const [metric, expected] of Object.entries({ "logical-running": "2", "logical-queued": "2", "execution-running": "3", "execution-queued": "3" })) {
    assert.equal(await page.locator(`[data-activity-metric="${metric}"] .activity-metric-value`).innerText(), expected);
  }
  assert.equal(await page.locator("tr[data-activity-job-id]").count(), 2);
  await record("activity.png", page.locator("main").screenshot({ animations: "disabled", caret: "hide", scale: "device" }));

  await navigate(`/jobs/${manifest.failureJobId}#operations`);
  await page.getByRole("tab", { name: "Operations", exact: true }).click();
  await page.getByRole("button", { name: "Analyze failures", exact: true }).click();
  await page.locator("[data-analyze-status]").filter({ hasText: "Analysis complete." }).waitFor();
  assert.match(await page.locator("[data-analyze-output]").innerText(), /No Copilot request was made/);
  await page.evaluate(() => window.scrollTo(0, 0));
  await ready();
  const tabs = await page.locator(".job-tabs").boundingBox();
  const analysis = await page.locator("[data-analyze-card]").boundingBox();
  await record("copilot-failure-analysis.png", page.screenshot({
    clip: { x: tabs.x, y: tabs.y, width: tabs.width, height: Math.ceil(analysis.y + analysis.height - tabs.y + 8) },
    fullPage: true, animations: "disabled", caret: "hide", scale: "device"
  }));

  await verifyFixture();
  assert.deepEqual(errors, []);
  assert.equal(captured.length, 5);
  await writeFile(path.join(runDirectory, "capture.json"), JSON.stringify({
    schemaVersion: 1,
    fixture: "synthetic-retail",
    nowUtc: manifest.nowUtc,
    browserVersion: browser.version(),
    viewport: { width: 1600, height: 1000, deviceScaleFactor: 1.5 },
    locale: "en-US",
    timezone: "UTC",
    logicalRunning: 2,
    runningExecutions: 3,
    jobCount: manifest.activityIds.length,
    screenshots: captured
  }, null, 2) + "\n", { flag: "wx" });
} finally {
  await browser.close();
}
