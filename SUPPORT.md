# Support

## How to file issues and get help

Use [GitHub Issues](https://github.com/microsoft/kusto-slice-runner/issues) for
bugs, feature requests, and usage questions. Search existing issues first. For
suspected vulnerabilities, follow [SECURITY.md](SECURITY.md) instead of opening
a public issue.

When asking for help, include:

- Kusto Slice Runner commit or branch.
- Command used to start the app.
- Scheduler enabled/disabled state.
- Kusto auth mode.
- Relevant `/api/v1/system/status` fields with secrets and local paths redacted
  as needed.
- Error messages with tokens, signatures, account keys, and connection strings
  redacted.

Do not attach SQLite databases, Kusto result exports, or browser traces. Provide
a minimal reproduction with fictional data instead.

## Microsoft Support Policy

Support for Kusto Slice Runner is limited to the resources listed above. This
developer-desktop tool is intended for non-production scenarios; it is not a
managed service and does not provide a support SLA.
