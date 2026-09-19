# Creating a KO Lite release

KO Lite separates hosted release creation from optional local AI highlights.
Pushes to `main` run CI but do not create releases.

## 1. Create the draft in GitHub Actions

1. Make sure the intended commit is on `main` and its CI checks are green.
2. Choose an unused `vMAJOR.MINOR.PATCH` version.
3. Open **Actions**, select **KO Lite Release**, choose **Run workflow**, keep
   the branch set to `main`, and enter the version.

The hosted workflow runs all quality gates, builds and smoke-tests both Windows
x64 packages, creates `SHA256SUMS.txt`, and creates a draft with three sections:
`Changes`, `Install`, and `Full Changelog`. The `Changes` section initially
contains a placeholder. The workflow does not publish the release.

## 2. Generate and review optional AI highlights

Install and authenticate GitHub CLI and GitHub Copilot CLI:

```powershell
gh auth login
copilot login
```

Invoke the `ko-lite-release-highlights` skill in GitHub Copilot CLI. When exactly
one strict-SemVer draft carrying the KO Lite release-workflow marker exists, the
skill selects it automatically. If multiple eligible drafts exist, choose from
the versions and URLs it presents. If none exist, run the release workflow
first. An explicitly requested `vMAJOR.MINOR.PATCH` remains supported.

The script requires PowerShell 7. It reads the selected draft, finds the
previous published release, retrieves the commits in that comparison range, and
gives their subjects to local Copilot with no tools available. The skill shows
the exact three-to-six generated bullets and requires approval before editing
GitHub. If `## Changes` already contains reviewed content rather than the
workflow placeholder, it shows that content and requires a distinct overwrite
confirmation.

After approval, the script revalidates the release id, draft state, target,
workflow marker, and complete body hash. It then replaces only `## Changes`,
verifies the updated draft, and removes its transient update data. A concurrent
manual edit detected before application invalidates the preview. Do not edit the
draft in another session during the brief apply step because GitHub's release
edit endpoint does not provide an atomic conditional-write precondition.

For a read-only preflight:

```powershell
pwsh -File .\scripts\New-KoLiteReleaseHighlights.ps1 -DryRun
```

The script's default mode still supports an explicit `-Version` and optional
`-OutputPath` for maintainers who deliberately want a local Markdown file;
existing files require `-Force`.

The approved write path cannot dispatch workflows, create or publish releases,
create tags, upload assets, or delete anything. It uses the existing interactive
sign-ins and requires no PAT, repository secret, or organization change.

## 3. Review and publish manually

1. Check every proposed highlight against the listed commits before approving
   the skill's draft edit.
2. Re-open the GitHub draft and confirm only `## Changes` changed.
3. Confirm these assets are present:
   - `ko-lite-<version>-win-x64-self-contained.zip`;
   - `ko-lite-<version>-win-x64-framework-dependent.zip`;
   - `SHA256SUMS.txt`.
4. Verify the ZIP hashes and smoke the self-contained package on clean Windows
   x64.
5. Publish the draft manually.

If local Copilot is unavailable, the output fails validation, or the draft
changes after preview, keep the GitHub-generated notes unchanged; the draft and
packages remain valid.

## Startup helpers in release packages

Both Windows package types include `Start-KoLiteApp.ps1`, `Stop-KoLiteApp.ps1`,
`KoLite.Startup.psm1`, `Register-KoLiteStartup.ps1`, `Get-KoLiteStartup.ps1`, and
`Unregister-KoLiteStartup.ps1` at the package root. Archive validation checks
that the startup helpers are present. Neither packaging nor extraction registers
a startup task.

Users can explicitly opt into startup at Windows sign-in, choosing the default
visible console or background mode. The per-user task/settings live outside
the replaceable app directory. A new extraction path needs re-registration;
an in-place upgrade at a stable path retains the registration. Follow the
[startup and upgrade precautions](operations-runbook.md#automatic-startup-at-windows-sign-in)
before replacing any running deployment.
