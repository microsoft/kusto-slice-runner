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
| `maxParallelism` | Yes | Minimum `1`; per-job active queue bound. |
| `queryTimeout` | Yes | Positive `TimeSpan`; used for Kusto server timeout and queue lease sizing. |
| `isPaused` | No | Defaults to `false`. Paused jobs do not schedule or claim queued retries. |
| `startFrom` | Yes | UTC ISO-8601 timestamp. After execution history exists, this field is read-only. |
| `endOn` | No | Optional UTC ISO-8601 timestamp. Must be greater than `startFrom` when present. |
| `folder` | No | Existing output/Kusto-oriented metadata. It is not a UI grouping tag. |
| `tags` | No | Optional array of job organization tags. Tags are trimmed, normalized to lowercase, deduplicated, and used by dashboard/catalog filters. |
| `dependsOn` | No | Array of dependency objects, each referencing an upstream by `id` (GUID) and/or `activityId`. KO Lite resolves `activityId` to the upstream's GUID and stores edges by `id`, so renames don't break dependencies. Self-dependencies are rejected. |
| `jobSettings` | No | Optional JSON value passed as the third function argument when non-empty. |
| `target.clusterUri` | Yes | Absolute HTTPS Kusto cluster URI. |
| `target.database` | Yes | Kusto database name. |

## UTC timestamp rules

`startFrom` and `endOn` must use `yyyy-MM-ddTHH:mm:ss[.fffffff][Z|+00:00]`. A missing offset is treated as UTC by the parser. Non-UTC offsets are rejected.

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

## Import/export behavior

The import page accepts a single schedule object or an array of schedule objects. Imports are
additive and update-only: an item matches an existing job by `id` when present (which is how a
**rename** is applied — same `id`, new `activityId`), otherwise by `activityId`; unmatched items
are created (a supplied `id` is preserved, else KO Lite mints one). Omitted jobs are left
untouched. `activityId` uniqueness is enforced across the catalog.

Exports are import-compatible and always include `id`. Export all emits every non-soft-deleted
job; row/detail export emits one job. Multi-job exports are emitted as a JSON array sorted in
ascending `activityId` (then `id`) order, so the output is deterministic and produces stable
diffs regardless of insertion order.
