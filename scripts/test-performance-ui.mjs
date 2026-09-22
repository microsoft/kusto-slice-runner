// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

const alpha = "11111111222233334444555566667777";
const beta = "888888889999aaaabbbbccccddddeeee";
const metricCells = Array.from({ length: 9 }, () =>
  '<td title="CPU P50: unavailable; 0 samples from 1 successful attempt." aria-label="CPU P50: unavailable; 0 samples from 1 successful attempt.">n/a</td>').join("");
const valueCells = `<td>100.0%<span>1 / 1</span></td>${metricCells}`;
const childIds = Array.from({ length: 32 }, (_, id) => `performance-chunk-${alpha}-${id}`);
const childRows = childIds.map((id, chunkId) =>
  `<tr id="${id}" data-performance-chunk-row data-performance-chunk-id="${chunkId}" hidden><th scope="row">Chunk ${chunkId}</th>${valueCells}</tr>`).join("");
const html = `<!doctype html><html><body>
  <a id="refresh" href="/activity?view=performance&range=7d&tag=ops&tag=daily&jobId=${alpha}&sort=cpu-p95&dir=desc" data-performance-state-link="true">Refresh</a>
  <section data-performance-root>
    <form method="get" action="/activity">
      <input name="view" value="performance" type="hidden">
      <input name="range" value="7d" type="hidden">
      <input name="tag" value="ops" type="hidden">
      <input name="tag" value="daily" type="hidden">
      <input name="jobId" value="${alpha}" type="hidden">
      <input name="sort" value="cpu-p95" type="hidden">
      <input name="dir" value="desc" type="hidden">
      <label>Job name <input name="q" value="Alpha" data-performance-filter-input></label>
    </form>
    <a id="range" href="/activity?view=performance&range=1d&tag=ops&tag=daily&jobId=${alpha}&sort=cpu-p95&dir=desc" data-performance-state-link="true">24 hours</a>
    <a id="sort" href="/activity?view=performance&range=7d&tag=ops&tag=daily&jobId=${alpha}&sort=memory-p50&dir=desc" data-performance-state-link="true">Memory P50</a>
    <a id="clear" href="/activity?view=performance&range=7d&sort=cpu-p95&dir=desc" data-performance-clear-filters>Clear filters</a>
    <p data-performance-filter-status role="status" aria-live="polite"></p>
    <div data-performance-coverage-warning data-performance-threshold-percent="20" data-performance-minimum-missing="5" hidden>
      <span data-performance-coverage-message></span>
    </div>
    <div tabindex="0" role="region" aria-label="Historical job performance">
      <table data-performance-table>
        <tbody data-performance-job-id="${alpha}" data-performance-search="Alpha" data-performance-eligible-attempts="25" data-performance-missing-attempts="5">
          <tr><th scope="row">
            <button type="button" aria-expanded="false" aria-controls="${childIds.join(" ")}" aria-label="Show chunks for Alpha" data-performance-disclosure data-performance-job-name="Alpha">Show chunks</button>
            <a href="/jobs/${alpha}">Alpha</a>
          </th>${valueCells}</tr>
          ${childRows}
        </tbody>
        <tbody data-performance-job-id="${beta}" data-performance-search="Beta" data-performance-eligible-attempts="75" data-performance-missing-attempts="0">
          <tr><th scope="row"><a href="/jobs/${beta}">Beta</a></th>${valueCells}</tr>
        </tbody>
      </table>
    </div>
    <p data-performance-no-matches hidden>No jobs match the job-name filter.</p>
    <div data-performance-footer>
      <section class="performance-collection"><h3>Collection details</h3><p data-performance-collection-error>Old global lookup error</p></section>
      <section class="performance-definitions"><h3>How these statistics are calculated</h3></section>
    </div>
  </section>
</body></html>`;

const dom = new JSDOM(html, {
  url: "http://127.0.0.1:5057/activity?view=performance&range=7d",
  runScripts: "outside-only"
});
const { window } = dom;
const requests = [];
window.fetch = async (...args) => {
  requests.push(args);
  throw new Error("Performance UI must not fetch data while filtering or expanding.");
};
const script = await readFile(new URL("../src/Ksr.LocalApp/wwwroot/js/site.js", import.meta.url), "utf8");
window.eval(script);

const document = window.document;
const groups = [...document.querySelectorAll("tbody[data-performance-job-id]")];
const button = document.querySelector("[data-performance-disclosure]");
const input = document.querySelector("[data-performance-filter-input]");
const rows = [...document.querySelectorAll("[data-performance-chunk-row]")];
const status = document.querySelector("[data-performance-filter-status]");
const noMatches = document.querySelector("[data-performance-no-matches]");
const warning = document.querySelector("[data-performance-coverage-warning]");
const coverageMessage = document.querySelector("[data-performance-coverage-message]");
assert.equal(rows.length, 32);
assert.ok([...document.querySelectorAll("tbody tr")].every(row => row.cells.length === 11));
assert.equal(document.querySelector("[data-performance-footer] details"), null);
assert.equal(document.querySelector("[data-performance-footer] summary"), null);
assert.ok(document.querySelector("[data-performance-table]").compareDocumentPosition(document.querySelector("[data-performance-footer]")) & window.Node.DOCUMENT_POSITION_FOLLOWING);
assert.deepEqual(rows.map(row => Number(row.getAttribute("data-performance-chunk-id"))), Array.from({ length: 32 }, (_, id) => id));
assert.ok(rows.every(row => row.hidden));
assert.equal(button.tabIndex, 0);
assert.equal(button.getAttribute("aria-expanded"), "false");
assert.ok(button.getAttribute("aria-controls").split(" ").every(id => document.getElementById(id)));
assert.equal(groups[0].hidden, false);
assert.equal(groups[1].hidden, true);
assert.equal(status.textContent, "Showing 1 of 2 job(s).");
assert.equal(warning.hidden, false);
assert.match(coverageMessage.textContent, /^5 of 25 eligible successful attempts \(20%\)/);
const initialCoverageMessage = coverageMessage.textContent;

button.focus();
button.dispatchEvent(new window.MouseEvent("click", { bubbles: true, detail: 0 }));
assert.equal(document.activeElement, button);
assert.equal(button.getAttribute("aria-expanded"), "true");
assert.equal(button.getAttribute("aria-label"), "Hide chunks for Alpha");
assert.ok(rows.every(row => !row.hidden));
assert.equal(coverageMessage.textContent, initialCoverageMessage, "Expanding child rows must not double-count coverage.");

input.focus();
input.value = "bEtA";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(document.activeElement, input);
assert.equal(groups[0].hidden, true);
assert.equal(groups[1].hidden, false);
assert.ok(rows.every(row => !row.hidden), "Filtering a group must preserve the child expansion state.");
assert.equal(button.getAttribute("aria-expanded"), "true");
assert.equal(warning.hidden, true);

input.value = "";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(document.activeElement, input);
assert.ok(groups.every(group => !group.hidden));
assert.ok(rows.every(row => !row.hidden));
assert.equal(status.textContent, "Showing 2 of 2 job(s).");
assert.equal(warning.hidden, true);
assert.match(coverageMessage.textContent, /^5 of 100 eligible successful attempts \(5%\)/);
assert.equal(document.querySelector("[data-performance-collection-error]").textContent, "Old global lookup error");
assert.equal(new URL(document.querySelector("#refresh").href).searchParams.has("q"), false);

input.value = "A & B / C";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(noMatches.hidden, false);
assert.equal(status.textContent, "Showing 0 of 2 job(s).");
assert.equal(warning.hidden, true);
for (const id of ["refresh", "range", "sort"]) {
  const url = new URL(document.getElementById(id).href);
  assert.equal(url.pathname, "/activity");
  assert.equal(url.searchParams.get("view"), "performance");
  assert.equal(url.searchParams.get("q"), input.value);
  assert.deepEqual(url.searchParams.getAll("tag"), ["ops", "daily"]);
  assert.equal(url.searchParams.get("jobId"), alpha);
}
assert.equal(new URL(document.querySelector("#range").href).searchParams.get("range"), "1d");
assert.equal(new URL(document.querySelector("#sort").href).searchParams.get("sort"), "memory-p50");
assert.equal(new URL(document.querySelector("#sort").href).searchParams.get("dir"), "desc");
assert.equal(new URL(document.querySelector("#clear").href).searchParams.has("q"), false);
assert.deepEqual(Array.from(new window.FormData(document.querySelector("form")).getAll("tag")), ["ops", "daily"]);

input.value = "";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
button.focus();
button.dispatchEvent(new window.MouseEvent("click", { bubbles: true, detail: 0 }));
assert.equal(button.getAttribute("aria-expanded"), "false");
assert.ok(rows.every(row => row.hidden));
assert.equal(document.activeElement, button);
assert.equal(requests.length, 0);
assert.equal(document.querySelectorAll("[data-performance-disclosure]").length, 1, "The unchunked group must not gain a disclosure.");

input.value = "Alpha";
for (const [eligible, missing, show] of [
  ["0", "0", false],
  ["3", "3", false],
  ["20", "4", false],
  ["26", "5", false],
  ["25", "5", true],
  ["25", "6", true],
  ["5", "5", true],
  ["10000", "1999", false],
  ["10000", "2000", true],
  ["9007199254740995", "1801439850948198", false],
  ["9007199254740995", "1801439850948199", true]
]) {
  groups[0].setAttribute("data-performance-eligible-attempts", eligible);
  groups[0].setAttribute("data-performance-missing-attempts", missing);
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
  assert.equal(warning.hidden, !show, `${missing}/${eligible} must use exact percentage and minimum gates.`);
  assert.ok(coverageMessage.textContent.startsWith(`${BigInt(missing).toLocaleString("en-US")} of ${BigInt(eligible).toLocaleString("en-US")}`));
}
groups[0].setAttribute("data-performance-eligible-attempts", "25");
groups[0].setAttribute("data-performance-missing-attempts", "5");
warning.setAttribute("data-performance-threshold-percent", "21");
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(warning.hidden, true, "The browser policy comes from server-rendered values.");
warning.setAttribute("data-performance-threshold-percent", "20");
warning.setAttribute("data-performance-minimum-missing", "6");
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(warning.hidden, true);
warning.setAttribute("data-performance-minimum-missing", "5");
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(warning.hidden, false);

const errors = [];
window.console.error = (...args) => errors.push(args);
groups[0].setAttribute("data-performance-missing-attempts", "invalid");
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(errors.length, 1, "Malformed coverage must be reported, not silently treated as zero.");
assert.equal(warning.hidden, false, "A parse failure must not turn the prior warning into a healthy state.");
groups[0].setAttribute("data-performance-missing-attempts", "26");
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(errors.length, 2);
assert.equal(warning.hidden, false);
assert.equal(requests.length, 0);
dom.window.close();
console.log("site.js Performance disclosure, scoped coverage, and filter tests passed.");
