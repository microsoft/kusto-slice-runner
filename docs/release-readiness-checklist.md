# Standalone repo readiness checklist

Use this checklist before copying `ko-lite` into its own internal repository or before sharing a release branch with new users.

## Tree hygiene

- Confirm only source, docs, scripts, config, solution, package, and workflow files are copied.
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
- Confirm the quickstart uses repository-relative paths, not machine-specific paths.
- Confirm the first-run path is scheduler-disabled or UI-only.
- Confirm live Kusto writes and required permissions are called out before any scheduler-enabled command.
- Confirm service setup says to publish first or pass `-AppDllPath`.

## Dependency and notice validation

- Confirm `package-lock.json` matches `package.json`.
- Confirm Chart.js assets under `src\KoLite.LocalApp\wwwroot\lib\chartjs` match the restored npm package when intentionally refreshed.
- Confirm `THIRD-PARTY-NOTICES.md` includes Chart.js and `@kurkle/color`.
- Confirm internal owner/support/security metadata is present and current.

## Publish smoke

```powershell
.\scripts\Publish-KoLiteLocalApp.ps1 -Configuration Release -Clean
.\scripts\Start-KoLitePublishedUi.ps1 -DryRun
.\scripts\Install-KoLiteLocalService.ps1 -DryRun
```

For a live smoke test, use a disposable SQLite database and start with scheduler disabled:

```powershell
.\scripts\Start-KoLitePublishedApp.ps1 -DatabasePath "$env:LOCALAPPDATA\KoLite\ko-lite-smoke.db" -SchedulerEnabled $false
```
