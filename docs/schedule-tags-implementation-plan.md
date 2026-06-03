# KO Lite top-level schedule tags backlog proposal

Status: proposal/backlog. Top-level schedule `tags` are not part of the current supported schedule contract.

## Problem and proposed approach

Add first-class job organization tags to KO Lite schedules using a top-level JSON field:

```json
{
  "tags": ["prod", "daily", "security"]
}
```

Tags are UI/catalog metadata, separate from Kusto ingestion tags and separate from the existing `folder` field. The existing `folder` field must remain reserved for its current output/Kusto meaning and should not be reused for job organization.

The implementation should add tags end-to-end through the schedule contract, import/export, field editor, read models, and dashboard/catalog UI. The first UI behavior should be tag filtering, not hierarchical grouping.

## Confirmed tag semantics

- `tags` is optional.
- If omitted or empty, the job has no tags.
- `tags` must be a JSON array of strings when present.
- Each tag is trimmed and normalized to lowercase.
- Empty/whitespace-only tags are invalid.
- Duplicate tags are removed case-insensitively after trim/lowercase normalization.
- Tags are mutable after a job starts, like non-identity metadata. They must not be blocked by started-job mutation policy.
- Tag filter behavior in the UI should use additive AND semantics when multiple tags are selected: a job must contain every selected tag to match.

## Current state from code analysis

- `JobDefinition` currently has no tags property.
- `ScheduleParser` has a strict supported top-level field set and currently rejects unknown `tags`.
- `ScheduleParser` already custom-validates array-shaped `dependsOn`; tags should follow the same explicit validation style rather than relying only on JSON DTO deserialization.
- `ScheduleFormInput` generates canonical schedule JSON from field-editor inputs and must learn how to round-trip tags.
- `SqliteJobCatalogRepository` persists canonical schedule JSON; no SQLite schema migration is required for basic tag support because tags can live in `schedule_json`.
- `DashboardPageQuery` already parses each catalog record into `JobDefinition`; tag filtering can be computed in-memory from parsed definitions for now.
- `_JobTable.cshtml` renders the main reusable job table and is the right place to show tag chips under the activity line.
- `Index.cshtml` currently builds active/completed/soft-deleted job tables without query-driven tag filters.
- `Catalog\Index.cshtml` uses the same dashboard data query and should either receive the same tag filtering behavior or an explicit catalog-scope tag filter.
- Existing "tag" mentions in Kusto code are ingestion tags and should not be touched.

## Implementation todos

1. Add core schedule tags

   Add `IReadOnlyList<string> Tags` to `JobDefinition`, allow `tags` in the supported top-level schedule contract, parse and validate `tags`, normalize tags with trim+lowercase, and deduplicate normalized tags. Add parser tests for valid tags, omitted tags, duplicate/case normalization, non-array tags, non-string tags, and blank tags.

2. Update schedule editing and JSON generation

   Add a `Tags` field to `ScheduleFormInput`, round-trip tags from `JobDefinition`, and write normalized tags back into generated schedule JSON only when at least one tag is present. Add a simple editor input in `_ScheduleEditor.cshtml`, likely a comma/newline/semicolon-separated text area or input with help text.

3. Preserve import/export compatibility

   Ensure single-object and array import continue to accept existing schedules without tags and preserve/add tags through canonical schedule JSON. Update import/export or catalog repository tests if existing coverage does not prove round-trip behavior.

4. Add tag filter read-model support

   Extend `DashboardPageData` with selected tag state and available tag summaries, or add a focused read-model type for tag filters. Compute available tags from visible non-soft-deleted jobs unless the UI needs to include soft-deleted counts too. Apply selected tags with AND semantics to active/completed/soft-deleted sections consistently.

5. Add dashboard/catalog UI filtering

   Add tag chips or filter links near the dashboard job table header and catalog page header. Preserve current explicit job actions and avoid adding bulk filtered destructive actions. Include selected tags in generated links so filters are bookmarkable and range links continue to preserve current dashboard range.

6. Display job tags in tables

   Update `_JobTable.cshtml` to show tags as compact chips under the activity line. Keep the existing `folder` display behavior separate and consider relabeling it only if needed to avoid confusion; do not treat `folder` as tags or grouping.

7. Update tests and docs

   Update core schedule parser tests, schedule mutation policy tests if needed, LocalApp web tests for tag display/filtering, and README schedule/import notes with the new `tags` field. Keep Kusto execution tests unchanged unless a compile break reveals a constructor/object initializer update is needed.

8. Validate the solution

   Run targeted tests first:

   ```powershell
   dotnet test .\tests\KoLite.Local.Core.Tests\KoLite.Local.Core.Tests.csproj
   dotnet test .\tests\KoLite.LocalApp.Tests\KoLite.LocalApp.Tests.csproj
   ```

   Then run the normal KO Lite validation:

   ```powershell
   dotnet build .\KoLite.Local.sln
   dotnet test .\KoLite.Local.sln --no-build
   ```

## Notes and considerations

- No live Kusto calls are required for this feature.
- No destructive SQLite migration is needed for the initial implementation.
- Filtering from parsed schedule JSON is acceptable for the local-first app unless performance becomes a problem with large catalogs; if needed later, tags can be indexed into a separate SQLite table.
- Keep top-level `tags` distinct from Kusto `.set-or-append` ingestion tags and from the existing `folder` schedule field.
- Do not introduce replacement/delete semantics in import. Existing additive/update-only import behavior should remain unchanged.
