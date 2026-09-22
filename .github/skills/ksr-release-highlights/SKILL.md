---
name: ksr-release-highlights
description: "Use when the user wants AI-written highlights for an existing Kusto Slice Runner GitHub Release draft. Auto-selects one workflow-owned draft or asks the user to choose among multiple drafts, previews the exact highlights for approval, and can replace only the draft's Changes section. Never creates, publishes, deletes, tags, uploads assets, reruns, or repairs a release."
metadata:
  author: Azure Core Team
  version: "2.0.0"
---

# Kusto Slice Runner release highlights

Use this skill to generate, review, and apply concise highlights to an existing
workflow-owned Kusto Slice Runner GitHub Release draft:

```powershell
pwsh -File .\scripts\New-KsrReleaseHighlights.ps1 -PrepareUpdate
```

## When to activate

Activate when the user asks for AI release highlights or a summary for an
existing Kusto Slice Runner release draft.

If the user supplies a version, require strict `vMAJOR.MINOR.PATCH` form and pass
it explicitly. Never infer or increment a version.

## Workflow

1. Resolve the repository root.
2. Run `scripts\New-KsrReleaseHighlights.ps1 -PrepareUpdate` with PowerShell
   7 (`pwsh`). Add `-Version vMAJOR.MINOR.PATCH` only when the user supplied or
   selected that version.
3. If no workflow-owned draft exists, stop and tell the user to run the
   **Kusto Slice Runner Release** workflow first. Do not ask for or invent a version.
4. If multiple workflow-owned drafts exist, use `ask_user` to present the exact
   versions and URLs printed by the script, then rerun `-PrepareUpdate` with the
   selected version.
5. Read the transient JSON update plan printed by the script. Show the user the
   exact `highlights` value and use `ask_user` to require explicit approval
   before any GitHub write. Tell the user not to edit the draft concurrently
   during the brief apply step because GitHub release edits do not expose an
   atomic conditional-write precondition.
6. If `changesWasPlaceholder` is false, also show the existing
   `originalChanges` value and require a distinct confirmation that it may be
   overwritten.
7. If the user declines or cancels, delete only the exact transient update-plan
   path and stop without changing GitHub.
8. After approval, apply the reviewed plan:

   ```powershell
   pwsh -File .\scripts\New-KsrReleaseHighlights.ps1 `
       -ApplyUpdatePlan <path> `
       -ConfirmDraftEdit
   ```

   Add `-AllowOverwriteChanges` only after the user explicitly approved
   replacing non-placeholder Changes content.
9. Return the draft URL printed by the script and state that the release remains
   a draft. The script deletes the consumed update plan after successful
   verification.

For a read-only preflight, pass `-DryRun`. The skill does not retain a Markdown
copy. If preparation or application ends in a terminal failure, delete only the
exact transient update-plan path before ending the task.

Generated top-level Markdown bullets start flush left, and each bullet is a
single unbroken line. The script normalizes incidental leading spaces or tabs
before bullet markers, and joins any hard-wrapped continuation lines back into
their bullet.

## Safety boundaries

- Edit only the `## Changes` section of a workflow-owned draft after explicit
  preview approval.
- Never edit a published release.
- Never create, publish, delete, tag, upload assets, rerun, or repair a release.
- Never dispatch or reconstruct the release workflow.
- Never call GitHub Models or create a PAT/repository secret.
- Never bypass the script's stale-body, ownership-marker, or overwrite guards.
- Never apply generated text without the required user approval.
- Never apply while the user is concurrently editing the same draft.

The hosted release workflow remains responsible for builds, tests, packages,
checksums, deterministic notes, and draft creation. The local script reads the
draft and the commits since the previous published release, prepares the
reviewable highlights, and performs only the approved Changes-section update.
