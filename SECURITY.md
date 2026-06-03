# Security

KO Lite is an internal tool that can execute live Kusto writes. Treat configuration, schedule JSON, logs, SQLite databases, and screenshots as potentially sensitive.

## Reporting

Report suspected security issues through the internal owner/support channel for this repo. Do not include secrets, bearer tokens, connection strings, database files, or confidential query results in issue text or screenshots.

## Handling sensitive data

- Do not commit SQLite databases, logs, Kusto outputs, credentials, or local diagnostic artifacts.
- Prefer redacted error summaries when sharing failures.
- Review schedule targets before enabling scheduler dispatch.
- Use managed identity for service-style runs when that is the approved internal operating model.

## Dependency checks

Run these before sharing release branches:

```powershell
dotnet list .\KoLite.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```
