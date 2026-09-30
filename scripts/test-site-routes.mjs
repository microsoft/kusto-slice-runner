// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

const html = `<!doctype html>
<html>
<body>
  <div class="table-wrap">
    <table class="ksr-table job-table-dashboard" data-dashboard-resizable="true">
      <colgroup><col data-column-key="activity" data-default-width="200" data-min-width="72" /></colgroup>
      <thead><tr><th>Activity<span class="column-resize-handle" data-column-key="activity"></span></th></tr></thead>
    </table>
  </div>
  <figure data-chartjs-throttle="retired-throttle-chart">
    <canvas id="retired-throttle-chart"></canvas>
    <script type="application/json" id="retired-throttle-chart-data">{"points":[]}</script>
  </figure>
  <figure data-chartjs-activity="executions-processed-chart">
    <canvas id="executions-processed-chart"></canvas>
    <script type="application/json" id="executions-processed-chart-data">{"rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T02:00:00Z","bucketMs":3600000,"asOfUtc":"2026-01-01T01:17:00Z","currentBucketStartUtc":"2026-01-01T01:00:00Z","points":[{"x":1767225600000,"succeeded":3,"failed":1,"total":4,"bucket":"2026-01-01T00:00:00Z","label":"2026-01-01T00:00:00Z"},{"x":1767229200000,"succeeded":2,"failed":0,"total":2,"bucket":"2026-01-01T01:00:00Z","label":"2026-01-01T01:00:00Z"}]}</script>
  </figure>
  <figure data-chartjs-success="success-rate-by-function-chart">
    <canvas id="success-rate-by-function-chart"></canvas>
    <script type="application/json" id="success-rate-by-function-chart-data">{"rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T02:00:00Z","bucketMs":3600000,"asOfUtc":"2026-01-01T01:17:00Z","currentBucketStartUtc":"2026-01-01T01:00:00Z","series":[{"name":"Example","points":[{"x":1767229200000,"y":50,"numerator":1,"denominator":2,"bucket":"2026-01-01T01:00:00Z","percentText":"50.0%"}]}]}</script>
  </figure>
  <figure data-chartjs-success="success-rate-complete-chart">
    <canvas id="success-rate-complete-chart"></canvas>
    <script type="application/json" id="success-rate-complete-chart-data">{"rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T02:00:00Z","bucketMs":3600000,"asOfUtc":"2026-01-01T02:00:00Z","currentBucketStartUtc":null,"series":[{"name":"Complete","points":[{"x":1767229200000,"y":100,"bucket":"2026-01-01T01:00:00Z"}]}]}</script>
  </figure>
  <figure data-chartjs-job="job-attempt-result-chart">
    <canvas id="job-attempt-result-chart"></canvas>
    <script type="application/json" id="job-attempt-result-chart-data">{"kind":"result-counts","rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T02:00:00Z","bucketMs":3600000,"asOfUtc":"2026-01-01T01:17:00Z","currentBucketStartUtc":"2026-01-01T01:00:00Z","series":[{"name":"Success","points":[{"x":1767229200000,"y":2,"count":2,"bucket":"2026-01-01T01:00:00Z"}]}]}</script>
  </figure>
  <figure data-chartjs-job="job-successful-duration-chart">
    <canvas id="job-successful-duration-chart"></canvas>
    <script type="application/json" id="job-successful-duration-chart-data">{"kind":"duration","rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T02:00:00Z","bucketMs":3600000,"asOfUtc":"2026-01-01T01:17:00Z","currentBucketStartUtc":"2026-01-01T01:00:00Z","series":[{"name":"Successful attempt duration","points":[{"x":1767229200000,"y":1200,"count":2,"bucket":"2026-01-01T01:00:00Z","durationText":"00:00:01.200"}]}]}</script>
  </figure>
  <figure data-chartjs-activity="executions-processed-legacy-chart">
    <canvas id="executions-processed-legacy-chart"></canvas>
    <script type="application/json" id="executions-processed-legacy-chart-data">{"rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T01:00:00Z","bucketMs":3600000,"points":[{"x":1767225600000,"succeeded":3,"failed":1,"total":4,"bucket":"2026-01-01T00:00:00Z"}]}</script>
  </figure>
  <figure data-dependency-graph
          data-dependency-graph-focal="11111111222233334444555566667777"
          data-kusto-lineage-url="/api/v1/dependency-graphs/kusto-lineage">
    <button data-dependency-graph-resolve type="button">Resolve</button>
    <span data-dependency-graph-status></span>
    <div data-dependency-graph-viewport></div>
    <script type="application/json" data-dependency-graph-data>{"nodes":[],"edges":[],"legend":[]}</script>
  </figure>
  <section data-analyze-card
           data-analysis-start-url="/ui-api/v1/jobs/11111111-2222-3333-4444-555555556666/failure-analyses">
    <button data-analyze-failures type="button">Analyze</button>
    <div data-analyze-status hidden></div>
    <div data-analyze-output hidden></div>
    <input name="__RequestVerificationToken" value="csrf-token" />
  </section>
</body>
</html>`;

const dom = new JSDOM(html, {
  url: "http://127.0.0.1:5057/jobs/11111111-2222-3333-4444-555555556666",
  runScripts: "outside-only"
});
const requests = [];
const charts = [];
dom.window.Chart = function (canvas, config) {
  charts.push({ canvas, config });
  return { data: config.data };
};
dom.window.fetch = async (url, options = {}) => {
  requests.push({ url, options });
  if (url === "/api/v1/dependency-graphs/kusto-lineage") {
    return {
      ok: true,
      status: 200,
      json: async () => ({ nodes: [], edges: [], legend: [] })
    };
  }
  if (url === "/ui-api/v1/jobs/11111111-2222-3333-4444-555555556666/failure-analyses") {
    return {
      ok: true,
      status: 200,
      json: async () => ({
        runId: "run-1",
        status: "Completed",
        markdown: "No failures."
      })
    };
  }
  throw new Error(`Unexpected fetch URL: ${url}`);
};

const siteScript = await readFile(
  new URL("../src/Ksr.LocalApp/wwwroot/js/site.js", import.meta.url),
  "utf8");
const columnStorageKey = "ksr.dashboard.job-table.column-widths.v1";
dom.window.localStorage.setItem(columnStorageKey, JSON.stringify({ activity: 240 }));
dom.window.eval(siteScript);

const activityColumn = dom.window.document.querySelector("col[data-column-key='activity']");
assert.equal(activityColumn.style.width, "240px");
const resizeHandle = dom.window.document.querySelector(".column-resize-handle");
resizeHandle.setPointerCapture = () => {};
resizeHandle.dispatchEvent(new dom.window.MouseEvent("pointerdown", { clientX: 100 }));
resizeHandle.dispatchEvent(new dom.window.MouseEvent("pointermove", { clientX: 160 }));
resizeHandle.dispatchEvent(new dom.window.MouseEvent("pointerup", { clientX: 160 }));
assert.equal(activityColumn.style.width, "300px");
assert.deepEqual(JSON.parse(dom.window.localStorage.getItem(columnStorageKey)), { activity: 300 });

assert.equal(charts.length, 6);
const chartById = Object.fromEntries(charts.map(chart => [chart.canvas.id, chart]));
const provisionalX = Date.parse("2026-01-01T01:17:00Z");
const provisionalIds = [
  "executions-processed-chart",
  "success-rate-by-function-chart",
  "job-attempt-result-chart",
  "job-successful-duration-chart"
];
for (const id of provisionalIds) {
  const point = chartById[id].config.data.datasets[0].data.at(-1);
  assert.equal(point.x, provisionalX, `${id} should plot its provisional value at the as-of time`);
  assert.equal(point.bucket, "2026-01-01T01:00:00Z");
}
assert.equal(chartById["executions-processed-chart"].config.data.datasets[0].data[0].x, 1767225600000);
assert.equal(chartById["success-rate-complete-chart"].config.data.datasets[0].data[0].x, 1767229200000);
assert.equal(chartById["executions-processed-legacy-chart"].config.data.datasets[0].data[0].x, 1767225600000);
assert.equal(chartById["executions-processed-chart"].config.options.scales.y.title.text, "Executions processed");
const activityTooltip = chartById["executions-processed-chart"].config.options.plugins.tooltip.callbacks;
assert.equal(
  activityTooltip.title([{ raw: { x: 1767225600000, bucket: "2026-01-01T00:00:00Z" } }]),
  "2026-01-01T00:00:00Z to 2026-01-01T01:00:00Z");
assert.equal(
  activityTooltip.label({ dataset: { label: "Succeeded" }, raw: { y: 1 } }),
  "Succeeded: 1 execution");
assert.equal(
  activityTooltip.label({ dataset: { label: "Succeeded" }, raw: { y: 3 } }),
  "Succeeded: 3 executions");
assert.equal(
  activityTooltip.footer([{ raw: { total: 4 } }]),
  "Total executions: 4");
assert.equal(
  activityTooltip.title([{ raw: chartById["executions-processed-chart"].config.data.datasets[0].data.at(-1) }]),
  "2026-01-01T01:00:00Z to 2026-01-01T02:00:00Z (provisional; as of 2026-01-01T01:17:00Z)");

for (const chart of charts) {
  const isProvisional = provisionalIds.includes(chart.canvas.id);
  const isLegacy = chart.canvas.id === "executions-processed-legacy-chart";
  assert.equal(chart.config.options.plugins.provisionalBucket.startUtc,
    isProvisional ? "2026-01-01T01:00:00Z" : isLegacy ? undefined : null);
  const rectangles = [];
  const context = {
    save() {},
    restore() {},
    fillRect(...args) { rectangles.push(args); }
  };
  const instance = {
    chartArea: { left: 10, right: 110, top: 20, bottom: 60 },
    scales: { x: { getPixelForValue(value) { return 10 + (value - 1767225600000) / 7200000 * 100; } } },
    ctx: context
  };
  const plugin = chart.config.plugins[0];
  plugin.beforeDraw(instance, {}, chart.config.options.plugins.provisionalBucket);
  assert.deepEqual(rectangles, isProvisional ? [[60, 20, 50, 40]] : []);
  if (isProvisional) assert.equal(context.fillStyle, "rgba(130, 80, 223, 0.12)");

  rectangles.length = 0;
  plugin.beforeDraw(instance, {}, { startUtc: "2025-12-31T23:00:00Z", endUtc: "2026-01-01T03:00:00Z" });
  assert.deepEqual(rectangles, [[10, 20, 100, 40]]);

  for (const options of [
    { startUtc: null, endUtc: "2026-01-01T02:00:00Z" },
    { startUtc: "invalid", endUtc: "2026-01-01T02:00:00Z" },
    { startUtc: "2026-01-01T03:00:00Z", endUtc: "2026-01-01T04:00:00Z" }
  ]) {
    rectangles.length = 0;
    plugin.beforeDraw(instance, {}, options);
    assert.deepEqual(rectangles, []);
  }
}

const rateTooltip = chartById["success-rate-by-function-chart"].config.options.plugins.tooltip.callbacks;
assert.equal(
  rateTooltip.title([{ raw: chartById["success-rate-by-function-chart"].config.data.datasets[0].data[0] }]),
  "2026-01-01T01:00:00Z to 2026-01-01T02:00:00Z (provisional; as of 2026-01-01T01:17:00Z)");
assert.equal(
  chartById["success-rate-complete-chart"].config.options.plugins.tooltip.callbacks.title([
    { raw: chartById["success-rate-complete-chart"].config.data.datasets[0].data[0] }
  ]),
  "2026-01-01T01:00:00Z to 2026-01-01T02:00:00Z");
assert.equal(
  chartById["executions-processed-legacy-chart"].config.options.plugins.tooltip.callbacks.title([
    { raw: { x: 1767225600000 } }
  ]),
  "2026-01-01T00:00:00Z to 2026-01-01T01:00:00Z");
for (const id of ["job-attempt-result-chart", "job-successful-duration-chart"]) {
  assert.equal(
    chartById[id].config.options.plugins.tooltip.callbacks.title([
      { raw: chartById[id].config.data.datasets[0].data[0] }
    ]),
    "2026-01-01T01:00:00Z to 2026-01-01T02:00:00Z (provisional; as of 2026-01-01T01:17:00Z)");
}
assert.equal(rateTooltip.label({
  dataset: { label: "Example" },
  raw: { y: 50, numerator: 1, denominator: 2, percentText: "50.0%" }
}), "Example: 50.0% (1/2)");
for (const id of provisionalIds) {
  const radius = chartById[id].config.data.datasets[0].pointRadius;
  assert.equal(radius({ raw: { y: null } }), 0);
  assert.equal(radius({ raw: { y: 0 } }), id === "success-rate-by-function-chart" ? 5.5 : 0);
  assert.ok(radius({ raw: { y: 1 } }) > 0);
}

dom.window.document.querySelector("[data-dependency-graph-resolve]").click();
dom.window.document.querySelector("[data-analyze-failures]").click();
await new Promise(resolve => dom.window.setTimeout(resolve, 25));

assert.equal(requests.length, 2);
assert.equal(requests[0].url, "/api/v1/dependency-graphs/kusto-lineage");
assert.equal(requests[0].options.method, "POST");
assert.deepEqual(JSON.parse(requests[0].options.body), {
  jobIds: ["11111111222233334444555566667777"]
});

assert.equal(
  requests[1].url,
  "/ui-api/v1/jobs/11111111-2222-3333-4444-555555556666/failure-analyses");
assert.equal(requests[1].options.method, "POST");
assert.equal(requests[1].options.headers["X-CSRF-TOKEN"], "csrf-token");
assert.match(
  dom.window.document.querySelector("[data-analyze-status]").textContent,
  /Analysis complete/);

console.log("site.js rendered-route tests passed.");
dom.window.close();
await import("./test-performance-ui.mjs");
