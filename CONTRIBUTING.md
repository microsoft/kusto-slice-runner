# Contributing

KO Lite is maintained as an internal standalone repo. Keep changes focused, safe for local-first operation, and covered by the existing solution validation.

## Development setup

```powershell
npm ci
dotnet restore .\KoLite.Local.sln
dotnet build .\KoLite.Local.sln --no-restore --nologo
dotnet test .\KoLite.Local.sln --no-build --nologo
dotnet format .\KoLite.Local.sln --verify-no-changes --no-restore --verbosity minimal
```

## Contribution guidelines

- Preserve live-Kusto safety warnings in docs and UI.
- Keep user-facing first-run examples scheduler-disabled unless the example is explicitly about live execution.
- Do not add fake/offline execution to the local app without a deliberate design review.
- Keep generated/runtime files out of source control.
- Update docs when changing schedule JSON, scripts, configuration, or operational behavior.
- Add or update tests for scheduler, worker, SQLite, Kusto request-building, import/export, and UI behavior changes.

## Pull request checklist

- Build, tests, and default-severity `dotnet format` pass.
- README and docs links still resolve.
- New third-party dependencies are reflected in `THIRD-PARTY-NOTICES.md`.
- Runtime artifacts such as SQLite databases, logs, `bin`, `obj`, and `.playwright-mcp` files are not included.
