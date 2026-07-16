---
name: ko-lite-release-highlights
description: "Use when the user wants AI-written highlights for an existing KO Lite GitHub Release draft. Requires an explicit vMAJOR.MINOR.PATCH version and invokes the repository's read-only highlights script, which writes a local Markdown file for manual review and paste. Never creates, edits, publishes, deletes, tags, reruns, or repairs a release."
metadata:
  author: Azure Core Team
  version: "1.0.0"
---

# KO Lite release highlights

Use this skill to generate a local Markdown file of concise highlights for an
existing KO Lite GitHub Release draft:

```powershell
pwsh -File .\scripts\New-KoLiteReleaseHighlights.ps1 -Version v1.1.0
```

## When to activate

Activate when the user asks for AI release highlights or a summary for an
existing KO Lite release draft.

Require an explicit version in strict `vMAJOR.MINOR.PATCH` form. Never infer or
increment a version.

## Workflow

1. Resolve the repository root.
2. Choose the user-requested output path, or allow the script to use its
   versioned `%TEMP%` default.
3. Run `scripts\New-KoLiteReleaseHighlights.ps1` with PowerShell 7 (`pwsh`) and
   the explicit version.
4. Return the output file path and draft URL printed by the script.
5. Tell the user to review the file and paste it above
   `## Complete generated notes` in the GitHub draft.

For a read-only preflight, pass `-DryRun`. Use `-Force` only when the user asks
to overwrite an existing local output file.

## Safety boundaries

- This skill and script are read-only toward GitHub.
- Never create, edit, publish, delete, tag, rerun, or repair a release.
- Never dispatch or reconstruct the release workflow.
- Never call GitHub Models or create a PAT/repository secret.
- Never paste or apply the generated text automatically.
- Do not proceed for a published release; highlights are generated before
  publication.

The hosted release workflow remains responsible for builds, tests, packages,
checksums, deterministic notes, and draft creation. The local script reads the
draft and the commits since the previous published release, then writes a local
Markdown file.
