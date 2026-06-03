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

## Fields

| Field | Required | Notes |
| --- | --- | --- |
| `activityId` | Yes | Stable job identity. After execution history exists, this field is read-only. |
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
| `dependsOn` | No | Array of dependency objects with `activityId`. Self-dependencies are rejected. |
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

Dependencies block downstream slice readiness until the corresponding upstream slice is complete. Dependency objects only support `activityId`.

## Tags

```json
"tags": ["prod", "daily", "security"]
```

Tags are local UI/catalog metadata for organizing jobs. They are separate from Kusto ingestion tags and separate from the `folder` field. When present, `tags` must be an array of non-empty strings. KO Lite trims each tag, normalizes it to lowercase, and removes duplicates after normalization. Dashboard and catalog tag filters use AND semantics when multiple tags are selected.

## Import/export behavior

The import page accepts a single schedule object or an array of schedule objects. Imports are additive and update-only: matching `activityId` values are updated, missing jobs are created, and omitted jobs are left untouched.

Exports are import-compatible. Export all emits every non-soft-deleted job; row/detail export emits one job.
