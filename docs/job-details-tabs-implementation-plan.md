# KO Lite job details tab layout and charts plan

## Problem and approach

The current single-job page at `ko-lite\src\KoLite.LocalApp\Pages\Jobs\Details.cshtml` already contains the four desired content areas, but they are stacked vertically: slice history, job definition, operations, and change history. The implementation should reorganize these existing areas into an accessible tabbed layout and enrich the default slice-history tab with per-job execution charts inspired by KO.web job details.

The safest approach is to keep the page fully server-rendered, preserve the existing header actions and `/jobs/{jobId}/history` full-history route, and add focused read models for the new charts rather than overloading the dashboard success-rate chart model.

## Current state

- `Pages\Jobs\Details.cshtml` renders:
  - page heading and job actions
  - `_SliceHistory` in a "Slice history" card
  - `_ScheduleEditor` in a "Job definition" section
  - recent attempts and recent logs/events in an "Operations" section
  - catalog definition diffs in a "Change history" section
- `Pages\Jobs\Details.cshtml.cs` loads `JobDetailsPageQuery.Get(jobId)` and builds the schedule editor view model.
- `Ui\JobDetailsPageQuery.cs` returns `JobDetailsPageData` with slice history, statuses, queue items, recent attempts/logs/events, catalog history, lifecycle, and started-state data.
- `Pages\Jobs\History.cshtml` reuses `JobDetailsPageQuery.Get(...)` with a large cell limit for full range browsing; this route should not pay for job-detail-only chart queries.
- `Ui\JobChartQuery.cs` currently only produces dashboard-level success-rate charts across all jobs.
- `Pages\Shared\_SuccessRateChart.cshtml`, `wwwroot\js\site.js`, and `wwwroot\css\site.css` already provide Chart.js rendering infrastructure for dashboard line charts.
- `slice_attempts` contains `status`, `started_at_utc`, `completed_at_utc`, and `metrics_json`; it has a slice/attempt index but not a `(job_id, completed_at_utc)` range index.
- LocalApp tests already assert current details-page sections, dashboard chart rendering, shared CSS/JS, and slice-history behavior.

## Proposed design

1. Keep one job-details route

   Preserve `@page "/jobs/{jobId}"`, the current top-level job title, pause/resume/delete/export/copy actions, and the full-history link. The tab interface should change layout, not route ownership or job action semantics.

2. Add an accessible tab shell

   Convert the current stacked sections into four tabs:

   - Slice history
   - Job definition
   - Operations
   - Change history

   Prefer anchor-addressable tabs such as `#slice-history`, `#definition`, `#operations`, and `#change-history` so operators can deep-link to a tab. Render all tab panels in the initial HTML so no-JS usage still exposes all content. Add light JavaScript only to improve active-tab switching, keyboard state, and focus handling.

3. Make Slice history the default tab

   The default tab should contain:

   - the existing `_SliceHistory` grid
   - the existing "Open full history" link to `/jobs/{jobId}/history`
   - a small chart range selector, likely reusing the dashboard options: 1 hour, 1 day, 7 days, 30 days
   - "Query Results by Time of Execution"
   - "Successful Query Duration by Time of Execution"

4. Add explicit per-job chart read models

   Extend `JobChartQuery` with a per-job method, or add a focused companion read model, that accepts `jobId` and range. Do not overload `SuccessRateChart`, because the dashboard charts are percentage-based success-rate charts and the new job charts are count/duration charts.

   Proposed models:

   - `JobAttemptResultChart` with binned count series for success, retry, and error outcomes.
   - `JobSuccessfulDurationChart` with duration points for successful attempts, binned by execution completion time.
   - `JobDetailsCharts` grouping both charts plus selected range metadata.

5. Define status taxonomy in one helper

   Use a single tested mapper for attempt statuses:

   - Success: `Succeeded`
   - Retry: retryable failures such as `FailedRetryable`
   - Error: terminal failures such as `Failed` and `DeadLettered`, plus `LeaseLost`

   Include help text explaining that `LeaseLost` is an operational failure included in the Error bucket for visibility, even though it is not always a direct Kusto query error.

6. Define duration semantics honestly

   Use actual query/Kusto duration from `metrics_json` when it is available. If that metric is unavailable, fall back to `completed_at_utc - started_at_utc` and label/help-text the fallback as successful attempt duration so the UI does not overstate it as engine query time.

7. Keep chart queries bounded and performant

   Query only the selected job and selected range. Prefer SQL aggregation for binned counts and bounded duration reads instead of loading every historical attempt into memory. Consider a non-destructive SQLite migration adding an index on `slice_attempts(job_id, completed_at_utc)` before relying on range queries for long-lived jobs.

8. Reuse chart rendering infrastructure carefully

   Keep Chart.js from `_Layout.cshtml`. Add separate partials and JavaScript builders for count/duration chart payloads, or generalize the current chart renderer without changing dashboard success-rate behavior. Since charts live in the default tab, hidden-canvas initialization is lower risk; if charts move to non-default tabs later, initialize or resize them on tab activation.

9. Preserve Operations and Change history content

   Move the existing recent attempts table and recent logs/events list into the Operations tab. Move the current catalog definition diff list into the Change history tab. Do not add new destructive operations or cleanup/rerun behavior to this layout change.

10. Update tests around behavior and layout

   Add or update tests in `tests\KoLite.LocalApp.Tests`:

   - chart query tests for per-job result counts, duration points, empty ranges, unknown statuses, and retry/error mapping
   - web tests asserting the tab labels, tab panel anchors, preserved existing sections, chart payload presence, and full-history link
   - web/static tests for tab CSS/JS hooks without breaking current dashboard chart assertions

## Implementation todos

1. Designing tab shell

   Refactor `Pages\Jobs\Details.cshtml` into tab navigation and tab panels while preserving all existing content and actions.

2. Adding tab styles and behavior

   Add accessible tab CSS and minimal JavaScript in `site.css` and `site.js`, including hash/deep-link behavior and no-JS-safe markup.

3. Modeling per-job charts

   Add explicit per-job chart DTOs and query methods for attempt result counts and successful duration series.

4. Optimizing chart reads

   Add a SQLite index/migration for `slice_attempts(job_id, completed_at_utc)` if the implementation needs range scans over attempts.

5. Rendering job charts

   Add Razor partials and Chart.js builders for the result-count and duration charts in the Slice history tab.

6. Wiring details page data

   Load chart data only for the job-details page, not for the full-history page that reuses `JobDetailsPageQuery`.

7. Updating tests

   Cover chart query semantics, web-rendered tabs, preserved current details content, and chart JavaScript/CSS hooks.

## Complexities and decisions

- The requested "Successful Query Duration" name may not match stored data if SQLite only has worker attempt timestamps. Use `metrics_json` actual Kusto timing when available; otherwise label the fallback as successful attempt duration.
- `LeaseLost` is an operational state rather than a straightforward query success/retry/error result; include it in the Error bucket with help text so the chart remains visible and the semantics stay explicit.
- Adding chart data directly to `JobDetailsPageQuery.Get()` would affect `/jobs/{jobId}/history`; chart loading should be separate or optional.
- Long-lived jobs could have many attempt rows. A bounded range, SQL aggregation, and possibly a new index are important for local SQLite responsiveness.
- Tab UI should remain safe and inspectable: all panels server-rendered, deep-linkable, keyboard-friendly, and no destructive job action hidden behind implicit client behavior.
- Existing dashboard success-rate chart behavior should remain unchanged; new chart models should not change dashboard semantics.

## Validation plan

- Run targeted LocalApp tests for chart query and web rendering.
- Run `dotnet build .\KoLite.Local.sln`.
- Run `dotnet test .\KoLite.Local.sln --no-build`.
