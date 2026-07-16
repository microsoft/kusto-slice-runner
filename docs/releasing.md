# Creating a KO Lite release

KO Lite separates hosted release creation from optional local AI highlights.
Pushes to `main` run CI but do not create releases.

## 1. Create the draft in GitHub Actions

1. Make sure the intended commit is on `main` and its CI checks are green.
2. Choose an unused `vMAJOR.MINOR.PATCH` version.
3. Open **Actions**, select **KO Lite Release**, choose **Run workflow**, keep
   the branch set to `main`, and enter the version.

The hosted workflow runs all quality gates, builds and smoke-tests both Windows
x64 packages, creates `SHA256SUMS.txt`, generates complete deterministic release
notes, and creates a draft. It does not publish the release.

## 2. Generate optional AI highlights locally

Install and authenticate GitHub CLI and GitHub Copilot CLI:

```powershell
gh auth login
copilot login
```

After the draft exists, run:

```powershell
pwsh -File .\scripts\New-KoLiteReleaseHighlights.ps1 -Version v1.1.0
```

The script requires PowerShell 7. It reads the draft, finds the previous
published release, retrieves the commits in that comparison range, and gives
their subjects plus the draft's `## Complete generated notes` section to local
Copilot with no tools available. It writes the non-empty response to a versioned
Markdown file under `%TEMP%` and prints the file path, draft URL, and commit
range.

Use `-OutputPath` to choose another file. Existing files are protected unless
`-Force` is supplied. `-DryRun` validates that the draft is readable without
invoking Copilot or writing a file.

The script is read-only toward GitHub. It does not dispatch workflows, edit
release notes, create tags, upload assets, publish, or delete anything. It uses
the existing interactive sign-ins and requires no PAT, repository secret, or
organization change.

## 3. Review and publish manually

1. Open the generated Markdown file.
2. Check every statement against the listed commits and
   `## Complete generated notes`.
3. Paste the bullets into an `## AI highlights` section immediately above
   `## Complete generated notes` in the draft.
4. Confirm these assets are present:
   - `ko-lite-<version>-win-x64-self-contained.zip`;
   - `ko-lite-<version>-win-x64-framework-dependent.zip`;
   - `SHA256SUMS.txt`.
5. Verify the ZIP hashes and smoke the self-contained package on clean Windows
   x64.
6. Publish the draft manually.

If local Copilot is unavailable or its output fails validation, keep the
GitHub-generated notes unchanged; the draft and packages remain valid.
