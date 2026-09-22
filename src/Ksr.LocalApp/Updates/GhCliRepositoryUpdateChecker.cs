// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ksr.LocalApp.Updates
{
    public sealed class GhCliRepositoryUpdateChecker : IRepositoryUpdateChecker
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
        private readonly ILogger<GhCliRepositoryUpdateChecker> logger;

        public GhCliRepositoryUpdateChecker(ILogger<GhCliRepositoryUpdateChecker> logger)
        {
            this.logger = logger;
        }

        public async Task<RepositoryUpdateCheckResult> CheckAsync(
            string repository,
            string? builtSha,
            CancellationToken cancellationToken)
        {
            var releaseResult = await RunGhAsync(
                $"api repos/{repository}/releases/latest --jq \"[.tag_name, .html_url] | @tsv\"",
                cancellationToken).ConfigureAwait(false);

            if (!releaseResult.Completed)
            {
                if (LooksLikeNotFound(releaseResult.ErrorMessage) &&
                    await RepositoryExistsAsync(repository, cancellationToken).ConfigureAwait(false))
                {
                    return RepositoryUpdateCheckResult.Failure(
                        UpdateCheckUnavailableReason.NoPublishedRelease,
                        $"No published release is available for {repository}.");
                }

                return RepositoryUpdateCheckResult.Failure(releaseResult.Reason, releaseResult.ErrorMessage);
            }

            if (!TryParseLatestRelease(releaseResult.StandardOutput, out var latestVersion, out var releaseUrl))
            {
                return RepositoryUpdateCheckResult.Failure(
                    UpdateCheckUnavailableReason.Unknown,
                    "GitHub returned incomplete latest release metadata.");
            }

            var encodedVersion = Uri.EscapeDataString(latestVersion);
            var shaResult = await RunGhAsync(
                $"api repos/{repository}/commits/{encodedVersion} --jq .sha",
                cancellationToken).ConfigureAwait(false);
            if (!shaResult.Completed)
            {
                return RepositoryUpdateCheckResult.Failure(shaResult.Reason, shaResult.ErrorMessage);
            }

            var latestSha = shaResult.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(latestSha))
            {
                return RepositoryUpdateCheckResult.Failure(
                    UpdateCheckUnavailableReason.Unknown,
                    "GitHub returned an empty commit SHA.");
            }

            if (string.IsNullOrWhiteSpace(builtSha) ||
                string.Equals(builtSha, latestSha, StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryUpdateCheckResult.Success(
                    latestSha,
                    RepositoryComparison.Identical,
                    commitsBehind: 0,
                    commitsAhead: 0,
                    latestVersion,
                    releaseUrl);
            }

            return await CompareAsync(
                repository,
                builtSha!,
                latestSha,
                latestVersion,
                releaseUrl,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<RepositoryUpdateCheckResult> CompareAsync(
            string repository,
            string builtSha,
            string latestSha,
            string latestVersion,
            string releaseUrl,
            CancellationToken cancellationToken)
        {
            var compareResult = await RunGhAsync(
                $"api repos/{repository}/compare/{builtSha}...{latestSha} --jq \"[.status, .ahead_by, .behind_by] | @tsv\"",
                cancellationToken).ConfigureAwait(false);

            if (compareResult.Completed &&
                TryParseComparison(compareResult.StandardOutput, out var status, out var aheadBy, out var behindBy))
            {
                var (comparison, commitsBehind, commitsAhead) = MapComparison(status, aheadBy, behindBy);
                return RepositoryUpdateCheckResult.Success(
                    latestSha,
                    comparison,
                    commitsBehind,
                    commitsAhead,
                    latestVersion,
                    releaseUrl);
            }

            // The remote HEAD is known, so the overall check still succeeds; the comparison is best-effort.
            if (!compareResult.Completed && LooksLikeUnknownCommit(compareResult.ErrorMessage))
            {
                // The built commit is not on the remote (e.g. an unpushed local build): treat as ahead.
                return RepositoryUpdateCheckResult.Success(
                    latestSha,
                    RepositoryComparison.LocalAhead,
                    commitsBehind: null,
                    commitsAhead: null,
                    latestVersion,
                    releaseUrl);
            }

            return RepositoryUpdateCheckResult.Success(
                latestSha,
                RepositoryComparison.Unknown,
                commitsBehind: null,
                commitsAhead: null,
                latestVersion,
                releaseUrl);
        }

        private async Task<bool> RepositoryExistsAsync(string repository, CancellationToken cancellationToken)
        {
            var result = await RunGhAsync(
                $"api repos/{repository} --jq .full_name",
                cancellationToken).ConfigureAwait(false);
            return result.Completed && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }

        public static (RepositoryComparison Comparison, int? CommitsBehind, int? CommitsAhead) MapComparison(
            string status,
            int aheadBy,
            int behindBy)
        {
            // base = built commit, head = remote branch.
            // ahead_by  = commits the remote branch is ahead of the build (i.e. we are behind).
            // behind_by = commits the remote branch is behind the build (i.e. we are ahead).
            return status.Trim().ToLowerInvariant() switch
            {
                "identical" => (RepositoryComparison.Identical, 0, 0),
                "ahead" => (RepositoryComparison.RemoteAhead, aheadBy, 0),
                "behind" => (RepositoryComparison.LocalAhead, 0, behindBy),
                "diverged" => (RepositoryComparison.Diverged, aheadBy, behindBy),
                _ => (RepositoryComparison.Unknown, null, null)
            };
        }

        public static bool TryParseLatestRelease(string output, out string version, out string releaseUrl)
        {
            version = string.Empty;
            releaseUrl = string.Empty;

            var line = output.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return false;

            var parts = line.Split('\t');
            if (parts.Length < 2) return false;

            version = parts[0].Trim();
            releaseUrl = parts[1].Trim();
            return !string.IsNullOrWhiteSpace(version) &&
                Uri.TryCreate(releaseUrl, UriKind.Absolute, out _);
        }

        private static bool TryParseComparison(string output, out string status, out int aheadBy, out int behindBy)
        {
            status = string.Empty;
            aheadBy = 0;
            behindBy = 0;

            var line = output.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return false;

            var parts = line.Split('\t');
            if (parts.Length < 3) return false;

            status = parts[0].Trim();
            if (string.IsNullOrEmpty(status)) return false;

            _ = int.TryParse(parts[1].Trim(), out aheadBy);
            _ = int.TryParse(parts[2].Trim(), out behindBy);
            return true;
        }

        private static bool LooksLikeUnknownCommit(string? errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText)) return false;
            var text = errorText.ToLowerInvariant();
            return text.Contains("404") ||
                text.Contains("no commit found") ||
                text.Contains("not found");
        }

        private static bool LooksLikeNotFound(string? errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText)) return false;
            var text = errorText.ToLowerInvariant();
            return text.Contains("404") || text.Contains("not found");
        }

        private async Task<GhProcessResult> RunGhAsync(string arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                logger.LogDebug(ex, "GitHub CLI (gh) could not be started.");
                return GhProcessResult.Unavailable(
                    UpdateCheckUnavailableReason.GhMissing,
                    "The GitHub CLI (gh) was not found on PATH.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return GhProcessResult.Unavailable(
                    UpdateCheckUnavailableReason.NetworkError,
                    $"The GitHub CLI did not respond within {ProcessTimeout.TotalSeconds:F0} seconds.");
            }

            if (process.ExitCode == 0)
            {
                return GhProcessResult.Ok(stdout.ToString());
            }

            var errorText = stderr.ToString().Trim();
            var reason = ClassifyFailure(errorText);
            logger.LogDebug("GitHub CLI exited with code {ExitCode}: {Error}", process.ExitCode, errorText);
            return GhProcessResult.Unavailable(reason, string.IsNullOrWhiteSpace(errorText) ? "The GitHub CLI returned an error." : errorText);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup; ignore failures killing the gh process.
            }
        }

        private static UpdateCheckUnavailableReason ClassifyFailure(string errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText)) return UpdateCheckUnavailableReason.Unknown;
            var text = errorText.ToLowerInvariant();

            if (text.Contains("not logged in") ||
                text.Contains("gh auth login") ||
                text.Contains("authentication") ||
                text.Contains("requires authentication") ||
                text.Contains("no oauth token"))
            {
                return UpdateCheckUnavailableReason.GhNotAuthenticated;
            }

            if (text.Contains("http 404") ||
                text.Contains("not found") ||
                text.Contains("http 403") ||
                text.Contains("must have admin") ||
                text.Contains("resource not accessible") ||
                text.Contains("permission"))
            {
                return UpdateCheckUnavailableReason.RepoAccessDenied;
            }

            if (text.Contains("could not resolve host") ||
                text.Contains("dial tcp") ||
                text.Contains("timeout") ||
                text.Contains("timed out") ||
                text.Contains("network is unreachable") ||
                text.Contains("connection refused"))
            {
                return UpdateCheckUnavailableReason.NetworkError;
            }

            return UpdateCheckUnavailableReason.Unknown;
        }

        private sealed record GhProcessResult(
            bool Completed,
            string StandardOutput,
            UpdateCheckUnavailableReason Reason,
            string? ErrorMessage)
        {
            public static GhProcessResult Ok(string standardOutput) =>
                new(true, standardOutput, UpdateCheckUnavailableReason.None, null);

            public static GhProcessResult Unavailable(UpdateCheckUnavailableReason reason, string? errorMessage) =>
                new(false, string.Empty, reason, errorMessage);
        }
    }
}
