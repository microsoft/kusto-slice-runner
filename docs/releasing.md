# Creating a KO Lite release

KO Lite uses manually dispatched, versioned GitHub Releases. Pushes to `main` continue to run CI but do not create permanent downloads or notify users that a new release is available.

## Prepare

1. Make sure the intended release commit is on `main` and its CI checks are green.
2. Choose an unused semantic version in `vMAJOR.MINOR.PATCH` form, such as `v1.2.0`.
3. Review the [release readiness checklist](release-readiness-checklist.md).

## Create the draft

1. Open **Actions** in `microsoft/kusto-slice-runner`.
2. Select **KO Lite Release**.
3. Choose **Run workflow**, keep the branch set to `main`, enter the version, and run it.

The workflow repeats the repository quality gates, stamps the exact source commit, creates and smoke-tests both Windows x64 packages, computes SHA-256 checksums, and creates a draft release. It never modifies a published release, an unrecognized draft, or a same-name tag at another commit.

If draft creation or an asset upload fails without a workflow-code change, use **Re-run jobs** on that same workflow run. The rerun can adopt its exact-SHA tag or workflow-marked draft, verifies the digest of every retained asset, and uploads only missing assets. Do not publish or manually edit the draft while its workflow is running. If GitHub reports an incomplete or mismatched draft asset, remove that draft asset as instructed by the workflow before rerunning.

A rerun always uses the workflow from the original commit. If the workflow itself was fixed after a failed run, do not rerun that old run: either review and publish its already-complete draft, or explicitly delete the draft and dispatch a new run from the updated `main`.

The release-note source is GitHub's generated change list since the previous published release. GitHub Models uses only that source to write concise highlights. Model inference is optional: if it is unavailable, rate-limited, or empty, the workflow still creates the draft with the deterministic GitHub-generated notes.

## Review the draft

Before publishing:

1. Confirm the version and target commit are correct. GitHub may leave a draft untagged and create the Git tag only when the draft is published.
2. Compare the highlights with the full change list and edit any unclear or unsupported wording.
3. Confirm these assets are present:
   - `ko-lite-<version>-win-x64-self-contained.zip`;
   - `ko-lite-<version>-win-x64-framework-dependent.zip`;
   - `SHA256SUMS.txt`.
4. Download the ZIPs and verify their hashes against `SHA256SUMS.txt`.
5. On a clean Windows x64 environment, extract the self-contained package and start it safely:

   ```powershell
   .\Start-KoLiteApp.ps1 -AppArguments '--KoLite:Scheduler:Enabled=false'
   ```

6. Check `/status/health`, then stop it through `Stop-KoLiteApp.ps1`.

Publish the draft only after the notes and assets pass review. Drafts are intentionally ignored by KO Lite's update checker; users see the new version only after publication.

## Package behavior

- The self-contained ZIP includes the .NET runtime and starts `KoLite.LocalApp.exe`.
- The framework-dependent ZIP requires the .NET 10 runtime and starts `KoLite.LocalApp.dll` through `dotnet`.
- Both packages include the PowerShell start/stop helpers, user-facing docs/notices, and project Copilot skills.
- Neither package contains local SQLite state. The default durable database remains `%LOCALAPPDATA%\KoLite\ko-lite.db` across application upgrades.
