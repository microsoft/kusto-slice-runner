---
name: ko-lite-schedule-json
description: "Use when the user wants to create, edit, or validate a KO Lite job-schedule JSON file (single object or an array of objects). Produces JSON-only output and validates it locally against the strict KO Lite schedule contract. Does NOT upload to KO Lite, write to Kusto, change schema, or run import tooling - uploading is the user's responsibility."
metadata:
  author: Azure Core Team
  version: "1.0.1"
---

# KO Lite schedule JSON

Use this skill to author or edit a KO Lite job-schedule JSON file. The
deliverable is **the JSON file only**. This skill never uploads, never writes
to Kusto, never invokes import tooling, and never modifies the catalog. The
user takes the file and imports it themselves through the local dashboard or
approved import tooling (see `README.md` and `docs\schedule-json.md`).

## When to activate

Activate when the user asks to:

- create a new KO Lite job schedule JSON file,
- edit an existing KO Lite schedule JSON file (single object or array),
- validate a KO Lite schedule JSON file against the strict KO Lite contract,
- scaffold a starter file from a template.

Do **not** activate for catalog operations such as listing, importing,
exporting, pausing, tombstoning, deleting, or resetting jobs — those go through
the local dashboard or approved import tooling.

## Authoritative contract

The KO Lite parser in `src\KoLite.Local.Core\Schedules\ScheduleParser.cs`
strictly rejects unknown top-level fields, unknown `target` fields, and
unknown `dependsOn` entry fields. Treat
`docs\schedule-json.md` as the source of truth and this
skill as a tight mirror of that contract.

### Allowed file shapes

- A single JSON object describing one job.
- A JSON array of such objects (the shape that KO Lite accepts for upsert-only
  batches).

Anything else is rejected by the validator.

### Top-level fields

| Field | Required | Type | Rules |
| --- | --- | --- | --- |
| `activityId` | Yes | string | Non-empty. Stable logical job id. Used in slice keys and dependency references. Repo convention: prefix with the logical job database name and a period (`<databaseName>.<activityName>`), for example `CopilotUsage.GhcpReportingUserDaily`. Dependency references must use the same full id. |
| `functionName` | Yes | string | Non-empty. Kusto producer function the worker calls per slice. KO Lite passes arguments positionally: slice start as the first `datetime` parameter, slice end as the second `datetime` parameter, and (when `jobSettings` is set) the settings as a third `dynamic` parameter. Parameter names are not inspected; recommended names are `startTime`, `endTime`, and `jobSettings`. |
| `outputTable` | Yes | string | Non-empty. Kusto table where worker output is committed. Convention in this repo: `outputTable` is `_` + `functionName` (leading underscore), so the producer function `Foo` writes to table `_Foo`. |
| `queryWindowSize` | Yes | TimeSpan string (`c` format) | `> 00:00:00`. Examples: `00:15:00`, `01:00:00`, `1.00:00:00`. |
| `delayFromUtcNow` | Yes | TimeSpan string (`c` format) | `>= 00:00:00`. Scheduler plans only slices whose end is at or before `utcNow - delayFromUtcNow`. |
| `maxParallelism` | Yes | integer | `>= 1`. Max active + newly planned slices per scheduler pass. |
| `queryTimeout` | Yes | TimeSpan string (`c` format) | `> 00:00:00`. Per-slice Kusto execution timeout. |
| `startFrom` | Yes | ISO-8601 UTC string | Shape `yyyy-MM-ddTHH:mm:ss[.fffffff][Z\|+00:00\|-00:00]` or no offset (treated as UTC). Non-UTC offsets are rejected. |
| `endOn` | No | ISO-8601 UTC string | Same shape rules as `startFrom`. When present, the scheduler caps planning so only whole, grid-aligned slices ending at or before `endOn` are emitted (no partial trailing slice; no KO-style mid-window clip). Must be strictly greater than `startFrom`. Mutable across `DefinitionVersion`s (unlike KO's `EndOn`). |
| `target` | Yes | object | `target.clusterUri` (absolute `https` URI, non-empty) and `target.database` (non-empty string). No other fields. |
| `isPaused` | No | boolean | Default `false`. When `true`, the scheduler emits no work. |
| `folder` | No | string | Informational only. KO Lite does not interpret. |
| `tags` | No | array of strings | Optional local job organization tags. When present, must be an array of non-empty strings. KO Lite trims tags, normalizes them to lowercase, deduplicates after normalization, and uses them for dashboard/catalog filters. Tags are separate from Kusto ingestion tags and from `folder`. |
| `dependsOn` | No | array of objects | Each entry is `{ "activityId": "<id>" }`. No bare-string shorthand. No self-dependency. |
| `jobSettings` | No | any JSON | Opaque pass-through for downstream code. KO Lite stores it but does not interpret it. |

Unknown fields anywhere in the top level, in `target`, or in any `dependsOn`
entry are **errors**, not warnings. Producing JSON with extra fields will fail
the validator and will fail in KO Lite at import time.

### TimeSpan formatting

KO Lite uses the .NET `c` (constant) format: `[d.]hh:mm:ss[.fffffff]`. Use
zero-padded components.

| Intent | String |
| --- | --- |
| 15 minutes | `00:15:00` |
| 1 hour | `01:00:00` |
| 1 day, 12 hours | `1.12:00:00` |
| 30 seconds | `00:00:30` |
| 500 milliseconds | `00:00:00.5000000` |

### `startFrom` formatting

Use one of:

- `2026-01-01T00:00:00Z`
- `2026-01-01T00:00:00+00:00`
- `2026-01-01T00:00:00` (no offset, treated as UTC)

Any other offset is rejected. Fractional seconds up to 7 digits are allowed.

### Bounded schedules (`endOn`)

`endOn` is optional. Omitting it (the default) means the schedule runs
indefinitely — same as KO Lite's existing behavior. When present, `endOn`
uses the same formatting rules as `startFrom` and must be strictly greater
than `startFrom`.

KO Lite enforces the bound at slice planning time: the scheduler caps
enumeration at `endOn` and `SliceEnumerator` then drops any partial
trailing window. A mid-window `endOn` is honored conservatively (the last
planned slice ends at the largest aligned boundary `<= endOn`); KO Lite
does **not** clip to a partial slice like KO does.

Once an `endOn`-bounded activity has contiguously completed through
`endOn` with no in-flight work, the scheduler emits a single
`ScheduleEnded` diagnostic per tick instead of planning more slices. The
activity is **not** auto-paused or tombstoned - that is an operator decision.

Downstreams of a bounded upstream are permanently capped at the
upstream's `endOn`. Give the downstream its own `endOn` (typically equal
to or later than the upstream's) when the bound is intentional.

## Workflow

1. **Confirm intent.** Is the user creating a new file, editing an existing
   file, or validating a file someone else produced? Confirm the target
   filesystem path for the output.
2. **Resolve required values.** For a new file, gather or propose:
   `activityId`, `functionName`, `outputTable`, `target.clusterUri`,
   `target.database`. Ask for anything that is genuinely ambiguous and cannot
   be inferred from the surrounding repo or conversation.
   Use the activity id convention `<databaseName>.<activityName>`; for example,
   jobs in the `CopilotUsage` namespace should be named `CopilotUsage.*`, not
   `CopilotUsage_*`.
   If the user mentions workstreams, environments, teams, features, or other
   local grouping labels, include them as optional `tags`; do not require tags
   when the user does not mention them.
3. **Pick a starting point.**
   - New single job: copy `templates\single-job.template.json`.
   - New batch: copy `templates\jobs-array.template.json`.
   - Edit: read the existing file in place; preserve key order and unrelated
     fields.
4. **Apply edits.** Use the `edit` tool to change specific values, or write
   the full file with `create` for brand-new files. Keep the JSON 2-space
   indented and end the file with a trailing newline.
5. **Validate locally.** Run `scripts\Test-KoLiteScheduleJson.ps1` against the
   final file. The validator mirrors the strict parser rules and exits non-zero
   on any failure.
6. **Report.** Tell the user where the file is, what it contains
   (`activityId`, target, schedule cadence), and what the next step is — they
   import through the local dashboard or approved import tooling. Do not run
   the import yourself.

## Validating the JSON

The validator is pure local PowerShell and does not contact Kusto.

```powershell
$repoRoot = [System.IO.Path]::GetFullPath((git rev-parse --show-toplevel))
& "$repoRoot\.github\skills\ko-lite-schedule-json\scripts\Test-KoLiteScheduleJson.ps1" `
    -Path .\path\to\my-job.json
```

Validate a JSON string (e.g., piping from another command):

```powershell
Get-Content .\path\to\my-job.json -Raw |
    & "$repoRoot\.github\skills\ko-lite-schedule-json\scripts\Test-KoLiteScheduleJson.ps1"
```

Capture results without exiting on failure:

```powershell
$results = & "$repoRoot\.github\skills\ko-lite-schedule-json\scripts\Test-KoLiteScheduleJson.ps1" `
    -Path .\jobs.json -Quiet -FailOnError:$false
$results | Where-Object { -not $_.IsValid } | ForEach-Object {
    "$($_.ActivityId): $($_.Errors -join '; ')"
}
```

The validator emits one PSCustomObject per definition with `IsValid`,
`ActivityId`, `Index`, and `Errors`. Both a single-object document and an
array of objects are accepted.

## Existing templates in this skill

Use these as references for shape and style:

- `.github\skills\ko-lite-schedule-json\templates\single-job.template.json`
  for a single schedule object.
- `.github\skills\ko-lite-schedule-json\templates\jobs-array.template.json`
  for an import-compatible array.

## Hard constraints

- **JSON output only.** Do not upload, do not run import tooling, do not run
  Kusto management commands, do not modify Kusto schema, do not write
  to `JobDefinitions` or any other catalog/state surface.
- **Strict fields only.** Never add fields outside the allowed set — the
  parser fails closed.
- **Append-only mindset.** A KO Lite schedule update is a new
  `DefinitionVersion` in Kusto; this skill produces the JSON that becomes the
  new version. Don't try to encode `DefinitionVersion`, `EventId`,
  `OperationId`, `IsDeleted`, or other catalog columns in the JSON — those are
  catalog metadata, not schedule fields.
- **No secrets.** The JSON describes a job, not credentials. Never embed
  managed-identity ids, connection strings, or storage keys.

## Stop and ask conditions

- The user has not provided (and the repo does not imply) a target Kusto
  cluster or database.
- The database-name prefix for `activityId` cannot be inferred from the file,
  folder, or surrounding docs.
- `activityId` collides with an existing job and the intent (rename vs. edit
  vs. supersede) is unclear.
- A requested schedule field is not in the supported contract (e.g., custom
  rerun intervals, chunk definitions, raw inline KQL, schema override switches,
  extent metadata controls, rebuild request types, performance request types).
  Explain the constraint and ask whether to drop the field or stop.
- `dependsOn` would create a self-dependency or an obvious cycle.

## Related docs

- `docs\schedule-json.md` - authoritative schedule contract and import/export
  behavior.
- `README.md` - KO Lite overview.
- `src\KoLite.Local.Core\Schedules\ScheduleParser.cs` - the parser whose
  rules this skill mirrors.
