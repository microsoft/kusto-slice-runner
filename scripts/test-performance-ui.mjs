import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

const alpha = "11111111222233334444555566667777";
const beta = "888888889999aaaabbbbccccddddeeee";
const childIds = Array.from({ length: 32 }, (_, id) => `performance-chunk-${alpha}-${id}`);
const childRows = childIds.map((id, chunkId) =>
  `<tr id="${id}" data-performance-chunk-row data-performance-chunk-id="${chunkId}" hidden><th scope="row">Chunk ${chunkId}</th><td>n/a</td></tr>`).join("");
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
    <a id="sort" href="/activity?view=performance&range=7d&tag=ops&tag=daily&jobId=${alpha}&sort=memory-p50&dir=asc" data-performance-state-link="true">Memory P50</a>
    <a id="clear" href="/activity?view=performance&range=7d&sort=cpu-p95&dir=desc" data-performance-clear-filters>Clear filters</a>
    <p data-performance-filter-status role="status" aria-live="polite"></p>
    <div tabindex="0" role="region" aria-label="Historical job performance">
      <table data-performance-table>
        <tbody data-performance-job-id="${alpha}" data-performance-search="Alpha">
          <tr><th scope="row">
            <button type="button" aria-expanded="false" aria-controls="${childIds.join(" ")}" aria-label="Show chunks for Alpha" data-performance-disclosure data-performance-job-name="Alpha">Show chunks</button>
            <a href="/jobs/${alpha}">Alpha</a>
          </th></tr>
          ${childRows}
        </tbody>
        <tbody data-performance-job-id="${beta}" data-performance-search="Beta">
          <tr><th scope="row"><a href="/jobs/${beta}">Beta</a></th></tr>
        </tbody>
      </table>
    </div>
    <p data-performance-no-matches hidden>No jobs match the job-name filter.</p>
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
const script = await readFile(new URL("../src/KoLite.LocalApp/wwwroot/js/site.js", import.meta.url), "utf8");
window.eval(script);

const document = window.document;
const groups = [...document.querySelectorAll("tbody[data-performance-job-id]")];
const button = document.querySelector("[data-performance-disclosure]");
const input = document.querySelector("[data-performance-filter-input]");
const rows = [...document.querySelectorAll("[data-performance-chunk-row]")];
const status = document.querySelector("[data-performance-filter-status]");
const noMatches = document.querySelector("[data-performance-no-matches]");
assert.equal(rows.length, 32);
assert.deepEqual(rows.map(row => Number(row.getAttribute("data-performance-chunk-id"))), Array.from({ length: 32 }, (_, id) => id));
assert.ok(rows.every(row => row.hidden));
assert.equal(button.tabIndex, 0);
assert.equal(button.getAttribute("aria-expanded"), "false");
assert.ok(button.getAttribute("aria-controls").split(" ").every(id => document.getElementById(id)));
assert.equal(groups[0].hidden, false);
assert.equal(groups[1].hidden, true);
assert.equal(status.textContent, "Showing 1 of 2 job(s).");

button.focus();
button.dispatchEvent(new window.MouseEvent("click", { bubbles: true, detail: 0 }));
assert.equal(document.activeElement, button);
assert.equal(button.getAttribute("aria-expanded"), "true");
assert.equal(button.getAttribute("aria-label"), "Hide chunks for Alpha");
assert.ok(rows.every(row => !row.hidden));

input.focus();
input.value = "bEtA";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(document.activeElement, input);
assert.equal(groups[0].hidden, true);
assert.equal(groups[1].hidden, false);
assert.ok(rows.every(row => !row.hidden), "Filtering a group must preserve the child expansion state.");
assert.equal(button.getAttribute("aria-expanded"), "true");

input.value = "";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(document.activeElement, input);
assert.ok(groups.every(group => !group.hidden));
assert.ok(rows.every(row => !row.hidden));
assert.equal(status.textContent, "Showing 2 of 2 job(s).");
assert.equal(new URL(document.querySelector("#refresh").href).searchParams.has("q"), false);

input.value = "A & B / C";
input.dispatchEvent(new window.Event("input", { bubbles: true }));
assert.equal(noMatches.hidden, false);
assert.equal(status.textContent, "Showing 0 of 2 job(s).");
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
assert.equal(new URL(document.querySelector("#sort").href).searchParams.get("dir"), "asc");
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
dom.window.close();
console.log("site.js Performance disclosure and filter tests passed.");
