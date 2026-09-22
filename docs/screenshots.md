# Recreating the documentation screenshots

The five README images show the real Kusto Slice Runner UI with **entirely synthetic data**.
They are not redacted captures of a live catalog. Job identities, schedules, tags,
workers, operational history, errors, and relationships all belong to a fictional
retail example. Lineage and failure analysis are deterministic fixture responses;
no Kusto or Copilot request is made.

## Prepare

Use a development checkout, preferably a separate worktree. Keep the normal app
running: this workflow neither uses nor stops it.

Install the SDK selected by `global.json`, PowerShell 7, and Node.js 22 or newer.
Restore the development dependencies in that checkout:

```powershell
npm ci
```

Install the pinned Playwright browser into this checkout's ignored artifact folder:

```powershell
$previousBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
try {
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path (Get-Location).Path 'artifacts\documentation-screenshots\browsers'
    node .\node_modules\playwright\cli.js install chromium --only-shell
    if ($LASTEXITCODE -ne 0) { throw 'Screenshot browser installation failed.' }
} finally {
    $env:PLAYWRIGHT_BROWSERS_PATH = $previousBrowserPath
}
```

Dependency/browser installation requires network access. Rendering the fixture does
not require Azure, GitHub, or Copilot sign-in. Browser installation is never part of
the application's startup or npm postinstall.

## Capture and review

From the checkout root:

```powershell
.\scripts\Capture-DocumentationScreenshots.ps1 -DryRun
.\scripts\Capture-DocumentationScreenshots.ps1
```

The script publishes `tests\Ksr.LocalApp.ScreenshotHost` into
`artifacts\documentation-screenshots\app`, then starts it at
`http://127.0.0.1:5107`. If that port is occupied, nothing is stopped; specify an
unused port with `-Port`. Port 5057 is refused.

Every invocation creates a fresh GUID-named directory under
`artifacts\documentation-screenshots\runs`. That directory owns its SQLite database,
data-protection keys, capture manifest, PNGs, and `capture.json` evidence. Existing
databases are never reused or reset. Logs are under the adjacent `logs` directory.

After all five captures, the script gracefully stops only the fixture it started.
If startup/capture fails, its owned child process is still cleaned up and the tracked
images are left unchanged. The published files and staging output remain available.
Run the recipe again for a fresh preview/capture; it does not install a background
service or a Windows sign-in task.

Review the printed staging directory. Then regenerate and replace the tracked images:

```powershell
.\scripts\Capture-DocumentationScreenshots.ps1 -UpdateImages
```

`npm run screenshots -- -DryRun` is an equivalent entry point. Capture without
`-UpdateImages` is the default so image review can precede documentation updates.
Do not use the ordinary app-start script on the screenshot deployment.

## What the fixture demonstrates

| Image | Example |
| --- | --- |
| `job-overview.png` | Ten fictional jobs with healthy, waiting, paused, and incomplete-history states; tag and text filters. |
| `job-detail.png` | Revenue slice history, a successful retry, a running window, upstream waits, and execution charts. |
| `dependency-graph-lineage.png` | Seven connected jobs, nine synthetic Kusto entities, and one undeclared dependency, rendered by the actual lineage UI. |
| `activity.png` | Two running logical windows and three running executions, including two active chunks of a four-chunk window; two queued windows and three queued executions. |
| `copilot-failure-analysis.png` | Three deliberately dead-lettered daily refund windows, a consistent schema-mismatch explanation, and an explicit illustrative-analysis notice. |

The fixture targets `https://ksr-example.invalid` / `RetailDemo`, a reserved,
non-routable example host. Do not substitute a real cluster or import the fixtures
into the live app. ADX deep links are displayed but never followed during capture.

The reference time is rounded down to the current five-minute UTC boundary, then
frozen for that run in the injected clock and browser. A recent anchor keeps the
existing history view's wall-clock lease checks consistent without changing product
code. Capture refuses fixtures older than 15 minutes. Regeneration preserves the
scenario and counts, not historical dates or byte-for-byte image identity.

The browser uses a 1600 x 1000 CSS-pixel viewport (1600 x 600 for the compact lineage
view), 1.5 device scale, English locale, UTC, and light theme. The script records
per-image viewports, dimensions, and SHA-256 hashes in `capture.json`, waits for
fonts/charts/graph/analysis readiness, and captures real elements or explicit page
crops. The detail crop ends after the query-results chart, matching the original
composition. It does not repaint content or inject substitute
HTML/CSS. An unavailable-update badge is expected because update checks are disabled.

## Safety and verification

The screenshot host uses `WebApplicationFactory<Program>` with a real loopback
Kestrel listener and the application's compiled Razor views/static assets.
Substitutions exist only in the developer host:

- Scheduler/worker dispatch, performance collection, retention, and update checks
  are disabled. The single-instance guard and normal local-request/antiforgery
  protections remain enabled.
- Existing repositories seed a fresh, owned SQLite database. No live database is
  opened, copied, migrated, or queried by the fixture.
- Local lineage and analysis providers supply only the fictional scenario.
  Other Kusto/CLI service paths throw if accidentally invoked.
- The capture verifies a per-run response identity and the effective database,
  disabled-service settings, and job count before interacting with the UI. Browser
  requests outside the fixture origin and unexpected writes are blocked.
- Capture checks visible links/text, page/network errors, graph and Activity counts,
  and PNG metadata. No raw browser traces, databases, logs, or machine-path manifests
  belong in `docs\images` or source control.

Before using regenerated images publicly, inspect **every PNG** for readable crops,
complete graphs/charts, and exclusively fictional content. Automated checks do not
replace visual review. Never upload old operational screenshots to an external OCR
or sanitization service.

Focused fixture coverage runs without installing a browser:

```powershell
dotnet test .\tests\Ksr.LocalApp.Tests\Ksr.LocalApp.Tests.csproj --filter "FullyQualifiedName~DocumentationScreenshotTests" --nologo
```

The normal app's project, runtime registrations, HTTP contracts, and release
packaging do not include the screenshot host or Playwright. Replacing these current
PNGs does not remove earlier image versions from Git history; a public-history
export still needs its separate historical-asset review.
