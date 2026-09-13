import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

const html = `<!doctype html>
<html>
<body>
  <figure data-chartjs-throttle="retired-throttle-chart">
    <canvas id="retired-throttle-chart"></canvas>
    <script type="application/json" id="retired-throttle-chart-data">{"points":[]}</script>
  </figure>
  <figure data-chartjs-activity="executions-processed-chart">
    <canvas id="executions-processed-chart"></canvas>
    <script type="application/json" id="executions-processed-chart-data">{"rangeStartUtc":"2026-01-01T00:00:00Z","rangeEndUtc":"2026-01-01T01:00:00Z","bucketMs":3600000,"points":[{"x":1767225600000,"succeeded":3,"failed":1,"total":4,"bucket":"2026-01-01T00:00:00Z","label":"2026-01-01T00:00:00Z"}]}</script>
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
  new URL("../src/KoLite.LocalApp/wwwroot/js/site.js", import.meta.url),
  "utf8");
dom.window.eval(siteScript);

assert.equal(charts.length, 1);
assert.equal(charts[0].canvas.id, "executions-processed-chart");
assert.equal(charts[0].config.options.scales.y.title.text, "Executions processed");
const activityTooltip = charts[0].config.options.plugins.tooltip.callbacks;
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
