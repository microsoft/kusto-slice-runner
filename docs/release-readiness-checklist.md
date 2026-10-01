# Standalone repo readiness checklist

Use this checklist before sharing a release with new users. Repository preparation
does not authorize public visibility or release publication.

## Public release gates

- Confirm the registered business/OSS approval is complete before changing visibility.
- Complete the division's current SDL/SFI, privacy, and applicable Responsible AI reviews.
- Complete required naming, trademark/icon, and PoliCheck reviews.
- Verify README purpose/state, Code of Conduct link, security reporting, third-party disclosure, and trademark notice.
- The Microsoft telemetry-notice clause is not applicable to this project's current feature set; no `PRIVACY` consent notice is required. Preserve README's factual data and external-service disclosures.
- Include `LICENSE.TXT` with the project name above copyright, `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, `SUPPORT.md`, `NOTICE`, and `THIRD-PARTY-NOTICES.md`.
- Reconcile NOTICE with the exact dependency/runtime/native inventory and release SBOM.
- Confirm Kusto SDK EULA redistribution terms and bundled Dagre/Graphlib attribution.
- Review inherited icons and screenshot branding; remove or obtain approval for product icons.
- Scan every published branch/tag's history, including old images, identities, and deleted files.
- Verify public-account attribution and the private original-to-filtered commit map.
- Repeat vulnerability checks when advisory feeds are reachable; cached restore is not an audit.

### Approval evidence (not established by passing CI)

| Gate | Required owner evidence | Status |
| --- | --- | --- |
| Naming/branding | Formal OSS name clearance for Kusto Slice Runner and the repository name; authorized product references/icons | Unverified |
| Microsoft source provenance | Permission from each authoring team for copied source, or explicit confirmation that no such source was copied | Unverified |
| Dependency redistribution | Legal/OSS confirmation of Kusto SDK EULA terms and exact package/runtime/native redistribution obligations | Unverified |
| Publication history | Approved secret/confidential-content review of every intended public branch/tag, including deleted files, commit messages, images, and attribution | Unverified |

Keep approval records in the private release review, not in public source. A naming
commit, supplied license text, empty secret-alert list, or successful build
does not complete these gates. The Bootstrap sprite's original copy revision is
unrecorded; review its adapted-path provenance instead of claiming an exact
upstream distribution version. Do not rewrite history without explicit approval.

## Tree hygiene

- Review first-party source/build headers and required root legal documents. Preserve original vendor attribution; do not add invalid comments to strict JSON or solution files.
- Text browser bundles and documentation PNGs are permitted; built Windows release binaries are assets, not checked-in source.
- Confirm only source, docs, config, solution, package, and workflow files are copied.
- Do not copy ignored `bin`, `obj`, `TestResults`, `.playwright-mcp`, SQLite database, log, publish, or local run artifacts.
- Confirm legacy generated-only directories are absent from the destination tree.
- Confirm `git status --short` is clean after restore/build/test commands.

## Local validation

```powershell
npm ci
dotnet restore .\Ksr.Local.sln
dotnet format .\Ksr.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\Ksr.Local.sln --no-restore --nologo
dotnet test .\Ksr.Local.sln --no-build --nologo
dotnet list .\Ksr.Local.sln package --vulnerable
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
- Confirm Chart.js assets under `src\Ksr.LocalApp\wwwroot\lib\chartjs`, Cytoscape.js assets under `src\Ksr.LocalApp\wwwroot\lib\cytoscape`, and marked assets under `src\Ksr.LocalApp\wwwroot\lib\marked` match the restored npm packages when intentionally refreshed.
- Confirm `NOTICE` and `THIRD-PARTY-NOTICES.md` include Chart.js, `@kurkle/color`, Cytoscape.js, cytoscape-dagre, bundled Dagre/Graphlib, marked, and the adapted Bootstrap Icons subset.
- Reconcile `NOTICE` with each final publish's resolved dependencies, native assets, and shipped .NET runtime packs; do not substitute a previous restore inventory.
- Preserve all required supplied licenses, notices, and EULAs in the distribution; license expressions and URLs are not automatic redistribution approval.
- Produce the organizationally required release SBOM with an approved tool.
- Confirm public support, contributor attribution, CLA, and security guidance are current.

## Publish smoke

```powershell
$publishDir = "$env:TEMP\ksr-publish"
dotnet publish .\src\Ksr.LocalApp\Ksr.LocalApp.csproj --configuration Release --output "$publishDir" --nologo
```

For a live smoke test, use a disposable SQLite database and start with scheduler disabled:

```powershell
$db = "$env:LOCALAPPDATA\Ksr\ksr-smoke.db"
dotnet "$publishDir\Ksr.LocalApp.dll" --ConnectionStrings:KsrSqlite="$db" --Ksr:Scheduler:Enabled=false --Ksr:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057/healthz`, then inspect `http://127.0.0.1:5057/api/v1/system/status` to confirm the database path and scheduler-disabled state. Stop the process before deleting the disposable database.

## GitHub Release draft

- Run **Kusto Slice Runner Release** from the Actions UI with an explicit unused
  `vMAJOR.MINOR.PATCH` version and the intended `main` commit.
- Confirm all normal hosted quality gates passed.
- Confirm both Windows x64 packages passed scheduler-disabled smoke tests:
  - self-contained executable;
  - framework-dependent DLL through .NET 10.
- Inspect both ZIPs for required notices/docs (including correctly cased `LICENSE.TXT`), startup helpers, `.github\skills`, and required dependency licenses; no SQLite databases, logs, credentials, `bin`, or `obj` directories.
- Download `SHA256SUMS.txt` and verify both ZIP hashes.
- Optionally invoke the `ksr-release-highlights` skill. It auto-selects one
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
