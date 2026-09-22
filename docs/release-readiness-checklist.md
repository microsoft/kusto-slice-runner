# Standalone repo readiness checklist

Use this checklist before sharing a release with new users. Repository preparation
does not authorize public visibility or release publication.

## Public release gates

- Confirm the registered business/OSS approval is complete before changing visibility.
- Complete the division's current SDL/SFI, privacy, and applicable Responsible AI reviews.
- Complete required naming, trademark/icon, and PoliCheck reviews.
- Verify README purpose/state, third-party disclosure, trademark notice, and applicable telemetry notice.
- Include `LICENSE.txt`, `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, `SUPPORT.md`, and `NOTICE`.
- Reconcile NOTICE with the exact dependency/runtime/native inventory and release SBOM.
- Confirm Kusto SDK EULA redistribution terms and bundled Dagre/Graphlib attribution.
- Review inherited icons and screenshot branding; remove or obtain approval for product icons.
- Scan every published branch/tag's history, including old images, identities, and deleted files.
- Verify public-account attribution and the private original-to-filtered commit map.
- Repeat vulnerability checks when advisory feeds are reachable; cached restore is not an audit.

## Tree hygiene

- Confirm only source, docs, config, solution, package, and workflow files are copied.
- Do not copy ignored `bin`, `obj`, `TestResults`, `.playwright-mcp`, SQLite database, log, publish, or local run artifacts.
- Confirm legacy generated-only directories are absent from the destination tree.
- Confirm `git status --short` is clean after restore/build/test commands.

## Local validation

```powershell
npm ci
dotnet restore .\KoLite.Local.sln
dotnet format .\KoLite.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\KoLite.Local.sln --no-restore --nologo
dotnet test .\KoLite.Local.sln --no-build --nologo
dotnet list .\KoLite.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```

Keep `dotnet format` at the default severity. Info-level analyzer cleanup is intentionally out of scope for the normal CI gate.

## Documentation validation

- Confirm every README relative link points to an existing file.
- Confirm README screenshots exist and are useful if screenshots are referenced.
- Confirm the quickstart uses repository-relative paths, not machine-specific paths.
- Confirm the first-run path is scheduler-disabled.
- Confirm live Kusto writes and required permissions are called out before any scheduler-enabled command.
- Confirm docs do not reference helper scripts unless those scripts exist in the checkout.

## Dependency and notice validation

- Confirm `package-lock.json` matches `package.json`.
- Confirm Chart.js assets under `src\KoLite.LocalApp\wwwroot\lib\chartjs`, Cytoscape.js assets under `src\KoLite.LocalApp\wwwroot\lib\cytoscape`, and marked assets under `src\KoLite.LocalApp\wwwroot\lib\marked` match the restored npm packages when intentionally refreshed.
- Confirm `THIRD-PARTY-NOTICES.md` includes Chart.js, `@kurkle/color`, Cytoscape.js, cytoscape-dagre, and marked.
- Confirm public support, contributor attribution, CLA, and security guidance are current.

## Publish smoke

```powershell
$publishDir = "$env:TEMP\ko-lite-publish"
dotnet publish .\src\KoLite.LocalApp\KoLite.LocalApp.csproj --configuration Release --output "$publishDir" --nologo
```

For a live smoke test, use a disposable SQLite database and start with scheduler disabled:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-smoke.db"
dotnet "$publishDir\KoLite.LocalApp.dll" --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057/healthz`, then inspect `http://127.0.0.1:5057/api/v1/system/status` to confirm the database path and scheduler-disabled state. Stop the process before deleting the disposable database.

## GitHub Release draft

- Run **Kusto Slice Runner Release** from the Actions UI with an explicit unused
  `vMAJOR.MINOR.PATCH` version and the intended `main` commit.
- Confirm all normal hosted quality gates passed.
- Confirm both Windows x64 packages passed scheduler-disabled smoke tests:
  - self-contained executable;
  - framework-dependent DLL through .NET 10.
- Confirm both ZIPs contain the start/stop scripts, required notices/docs, and `.github\skills`, but no SQLite databases, logs, credentials, `bin`, or `obj` directories.
- Download `SHA256SUMS.txt` and verify both ZIP hashes.
- Optionally invoke the `ko-lite-release-highlights` skill. It auto-selects one
  workflow-owned draft or asks you to choose among multiple eligible drafts.
- Review every generated bullet against the complete GitHub-generated notes,
  then explicitly approve the Changes-section edit. If Changes already contains
  reviewed content, separately confirm any overwrite.
- Re-open the draft and verify that `Install`, `Full Changelog`, the target, and
  all assets are unchanged.
- If Copilot is unavailable or validation fails, retain the deterministic notes.
- Confirm the draft targets the intended commit. GitHub may create the Git tag only when the draft is published, and the update badge will not see the version before publication.
- Download and run the self-contained package on a clean Windows x64 environment before publishing the draft.
- Publish only after manual review.
