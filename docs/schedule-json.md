# KO Lite schedule JSON

The schedule contract is intentionally strict. Unknown top-level fields, unknown `target` fields, and unknown dependency fields are rejected so imports stay predictable.

## Minimal shape

```json
{
  "activityId": "sample.hourly",
  "functionName": "BuildHourlySlice",
  "outputTable": "HourlyOutput",
  "queryWindowSize": "01:00:00",
  "delayFromUtcNow": "00:10:00",
  "maxParallelism": 2,
  "queryTimeout": "00:05:00",
  "isPaused": true,
  "healthPolicy": "complete",
  "description": "Builds the hourly data used by the sample dashboard.",
  "tags": ["prod", "daily"],
  "startFrom": "2026-01-01T00:00:00Z",
  "target": {
    "clusterUri": "https://cluster.kusto.windows.net",
    "database": "DatabaseName"
  }
}
```

A job's permanent identity is an opaque GUID `id` that KO Lite assigns. You normally omit `id`
when authoring a new job (KO Lite mints one); exports always include it. `activityId` is a
mutable, unique, human-facing label (a display name) — it can be renamed without affecting the
durable `id`, dependency edges, slice history, or output idempotency.

## Fields

| Field | Required | Notes |
| --- | --- | --- |
| `id` | No | Opaque GUID permanent identity. Omit when creating (KO Lite mints one); preserved on export so export→import round-trips and matches an existing job for rename. Immutable once assigned. |
| `activityId` | Yes | Mutable, unique, human-facing display label. May be renamed at any time. Used as a dependency alias and import match key when `id` is absent. |
| `functionName` | Yes | Kusto function to invoke for each slice. Must be a safe Kusto identifier when executed. |
| `outputTable` | Yes | Kusto table appended by `.set-or-append`. Must be a safe Kusto identifier when executed. |
| `queryWindowSize` | Yes | Positive `TimeSpan`; each slice covers this window size. |
| `delayFromUtcNow` | Yes | Non-negative `TimeSpan`; delays scheduling near-real-time windows. |
| `maxParallelism` | Yes | Minimum `1`, with no upper limit. Hard per-job concurrency bound measured in execution units: chunks for chunked jobs, otherwise logical slices. Enforced at claim time across scheduled work, retries, repairs, and recovery. When a cluster is under sustained ingestion throttling, the [throttling advisor](operations-runbook.md#ingestion-throttling-advisor) may recommend reducing this (never below the job's keep-up floor); reductions are applied only when an operator confirms them. |
| `queryTimeout` | Yes | Positive `TimeSpan`; used for Kusto server timeout and queue lease sizing. |
| `chunks` | No | Integer `1..32`. Presence splits every logical window into 0-based chunks and changes the Kusto function signature. Immutable after the job starts. |
| `isPaused` | No | Defaults to `false`. Paused jobs do not schedule or claim queued retries. |
| `healthPolicy` | No | `"complete"` (default) or `"recent"`. Controls how the dashboard scores this job's health. `complete` (strict) additionally surfaces unaddressed historical gaps (terminal dead-lettered slices) as a "N gaps" segment on the status pill, even when recent slices are healthy. `recent` colors purely by the recent-slice trend and ignores old gaps. See [Dashboard status](operations-runbook.md#dashboard-status-model). |
| `description` | No | Optional Markdown catalog metadata, limited to 65,536 characters. Rendered on the job details page with embedded raw HTML treated as text. Preserved by copy/import/export and catalog history; never passed to the Kusto function. |
| `startFrom` | Yes | UTC ISO-8601 timestamp. After execution history exists, this field is read-only. |
| `endOn` | No | Optional UTC ISO-8601 timestamp. Must be greater than `startFrom` when present. |
| `folder` | No | Existing output/Kusto-oriented metadata. It is not a UI grouping tag. |
| `tags` | No | Optional array of job organization tags. Tags are trimmed, normalized to lowercase, deduplicated, and used by dashboard/catalog filters. |
| `dependsOn` | No | Array of dependency objects, each referencing an upstream by `id` (GUID) and/or `activityId`. KO Lite resolves `activityId` to the upstream's GUID and stores edges by `id`, so renames don't break dependencies. Self-dependencies are rejected. |
| `jobSettings` | No | Optional JSON value passed after the time-window arguments; it is third when `chunks` is absent and fifth when `chunks` is present. |
| `target.clusterUri` | Yes | Absolute HTTPS Kusto cluster URI. |
| `target.database` | Yes | Kusto database name. |

## UTC timestamp rules

`startFrom` and `endOn` must use `yyyy-MM-ddTHH:mm:ss[.fffffff][Z|+00:00]`. A missing offset is treated as UTC by the parser. Non-UTC offsets are rejected.

## Chunks

When `chunks` is absent, KO Lite preserves the existing function signatures:

```kusto
MyFunction(startTime:datetime, endTime:datetime)
MyFunction(startTime:datetime, endTime:datetime, jobSettings:dynamic)
```

When `chunks` is present (including `chunks: 1`), KO Lite invokes the function once for every 0-based chunk:

```kusto
MyFunction(startTime:datetime, endTime:datetime, chunkId:long, chunks:long)
MyFunction(startTime:datetime, endTime:datetime, chunkId:long, chunks:long, jobSettings:dynamic)
```

A typical function partitions deterministically with `hash(PartitionKey, chunks) == chunkId`.
All chunks share the job's `maxParallelism`. A logical slice becomes `Completed` only after every
chunk completes, so downstream dependencies never run on partial upstream output.

Concurrency is counted per execution unit. For example, `chunks: 32` with
`maxParallelism: 8` permits at most 8 chunks of the job to run simultaneously; with
`maxParallelism: 32`, all 32 chunks of one window can run together when at least 32
global worker slots are free. `maxParallelism` values above 32 are valid and can overlap
chunks from later windows. KO Lite's global worker pool is unbounded by default, but an
operator-configured `KoLite:WorkerPool:MaxConcurrency` may impose a lower all-up limit.

Pause is immediate: already-running chunks finish, while unstarted chunks and retries wait for
resume. `chunks` cannot be added, removed, or changed after the job has any scheduling history;
create a new job identity when partition cardinality must change.

Each chunk has a distinct stable Kusto idempotency key and matching `ingest-by:` tag. The same
chunk reuses that identity across retry, repair, orphan recovery, and restart. Local queue keys are
separate and may vary by work source.

## Dependencies

```json
"dependsOn": [
  { "activityId": "upstream.hourly" }
]
```

Dependencies block downstream slice readiness until the corresponding upstream slice is complete.
A dependency entry may reference the upstream by `activityId` (human-friendly), by `id` (the
upstream's GUID, rename-safe and usable as a forward reference), or both. KO Lite resolves each
entry to the upstream's GUID and **stores the edge by `id`**, so renaming an upstream does not
break downstream dependencies. Referencing an upstream by `activityId` requires that upstream to
already exist (in the catalog or the same import batch); otherwise reference it by `id`. Exports
render each edge as `{ "id": ..., "activityId": ... }` for readability.

## Tags

```json
"tags": ["prod", "daily", "security"]
```

Tags are local UI/catalog metadata for organizing jobs. They are separate from Kusto ingestion tags and separate from the `folder` field. When present, `tags` must be an array of non-empty strings. KO Lite trims each tag, normalizes it to lowercase, and removes duplicates after normalization. Dashboard and catalog tag filters use AND semantics when multiple tags are selected.

## Description

`description` is optional freeform Markdown for explaining a job's purpose, ownership,
runbook links, or other local catalog context. KO Lite preserves accepted text exactly,
up to 65,536 characters, and renders it only on the job details page. Embedded raw HTML
is displayed as text; generated Markdown HTML is sanitized before it reaches the DOM.
The field is catalog metadata and is never included in Kusto function arguments or
request metadata.

## Import/export behavior

The import page accepts a single schedule object or an array of schedule objects. Imports are
additive and update-only: an item matches an existing job by `id` when present (which is how a
**rename** is applied — same `id`, new `activityId`), otherwise by `activityId`; unmatched items
are created (a supplied `id` is preserved, else KO Lite mints one). Omitted jobs are left
untouched. `activityId` uniqueness is enforced across the catalog.

Exports are import-compatible and always include `id`. Export all emits every non-soft-deleted
job; row/detail export emits one job. Multi-job exports are emitted as a JSON array sorted in
ascending `activityId` (then `id`) order, so the output is deterministic and produces stable
diffs regardless of insertion order. Export output is pretty-printed (indented) JSON for
readability; the compact JSON kept in local storage is unaffected.
