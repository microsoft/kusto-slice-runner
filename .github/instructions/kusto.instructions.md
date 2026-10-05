---
description: 'Kusto/KQL conventions for Kusto Slice Runner queries and scripts.'
applyTo: '**/*.csl, **/*.kql'
---

# Kusto / KQL Conventions

## Safety

- Assume Kusto operations are live and can affect configured output tables.
- Prefer read-only inspection first. Use management commands only when the user
  explicitly asks for schema or function changes.
- Do not run `.drop table`, `.clear table`, `.delete`, or similar destructive
  commands unless the user explicitly asks for that exact outcome.
- For validation, avoid materializing large result sets. Prefer small `take`
  limits, `| consume`, explicit server timeouts, and saved artifacts.

## File format

- Use `.csl` for Kusto management scripts, schema scripts, and function/table
  definitions.
- Use `.kql` for ad hoc query variants and investigation queries.
- Files end with a single trailing newline and no trailing whitespace.

## Naming

- `let` variable names: **camelCase** (for example, `startTime`, `binSize`).
- Function parameter names: **camelCase**.
- Table names, function names, and column names: **PascalCase** unless an
  existing Kusto object already requires a different spelling.

## Query structure

- Decompose multi-step queries into named `let` blocks rather than one long
  pipeline.
- Place each `;` on its own line, left-aligned with the corresponding `let`.
- Put the query body on a new line after `=` unless the right-hand side is a
  scalar.
- Each `|` starts on a new line at the same indentation as the previous pipe.
- Pipes inside a `join` subquery are indented relative to the outer query.
- For `project`, `extend`, and `summarize` with many fields, put each field on
  its own line.

```kusto
let queryStart =
    datetime(2026-01-01T00:00:00Z)
;
let sourceRows =
    SourceTable
    | where PreciseTimeStamp >= queryStart
    | project PreciseTimeStamp,
              ActivityId,
              Value
;
sourceRows
| summarize Count = count() by ActivityId
| order by ActivityId asc
```

## Filters and ordering

- Combine consecutive `where` operators unless splitting genuinely improves
  readability or is required for correctness.
- Use inclusive start and exclusive end time ranges:
  `PreciseTimeStamp >= startTime and PreciseTimeStamp < endTime`.
- Always specify `asc` or `desc` on `sort` and `order by`.
- For multi-line filters, put `and` / `or` at the start of continuation lines.

```kusto
T
| where PreciseTimeStamp >= startTime
    and PreciseTimeStamp <  endTime
    and ActivityId       == activityId
```

## Joins

- Prefer a named right-side dataset. If the right side is multi-line, assign it
  to a `let` first.
- Avoid `$left` / `$right`; rename fields before the join so the `on` clause
  stays simple.
- Wrap joined datasets in parentheses even when Kusto would allow omitting them.

```kusto
let dependencyRows =
    Dependencies
    | project ActivityId,
              DependencyActivityId
;
Jobs
| join kind = leftouter (
    dependencyRows
  ) on ActivityId
```

## Kusto Slice Runner-specific notes

- Kusto Slice Runner worker execution calls a configured Kusto function per
  slice. The function receives slice start and slice end as the first two
  `datetime` arguments, and receives `jobSettings` as a third `dynamic` argument
  when settings are configured.
- Output writes use `.set-or-append` against the schedule's `outputTable`.
  Preserve table data unless the user explicitly requests cleanup.
- Prefer idempotent schema commands such as `.create-merge table` and
  `.create-or-alter function`.
- Keep rerun cleanup commands reviewable. Kusto Slice Runner may suggest cleanup
  commands, but operators execute them deliberately.
