# Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit
[Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

Keep changes focused, safe for local-first operation, and covered by the existing solution validation.
Use fictional targets and data in examples, tests, and screenshots. Do not submit credentials,
live catalog exports, runtime databases, or unredacted diagnostic evidence.

## Development setup

```powershell
npm ci
dotnet restore .\Ksr.Local.sln
dotnet build .\Ksr.Local.sln --no-restore --nologo
dotnet test .\Ksr.Local.sln --no-build --nologo
dotnet format .\Ksr.Local.sln --verify-no-changes --no-restore --verbosity minimal
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
