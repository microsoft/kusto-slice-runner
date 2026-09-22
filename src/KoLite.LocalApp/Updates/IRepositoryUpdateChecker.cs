// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace KoLite.LocalApp.Updates
{
    public enum RepositoryComparison
    {
        Unknown,
        Identical,
        RemoteAhead,
        LocalAhead,
        Diverged
    }

    public sealed record RepositoryUpdateCheckResult(
        bool Succeeded,
        string? LatestSha,
        RepositoryComparison Comparison,
        int? CommitsBehind,
        int? CommitsAhead,
        UpdateCheckUnavailableReason FailureReason,
        string? ErrorMessage,
        string? LatestVersion,
        string? ReleaseUrl)
    {
        public static RepositoryUpdateCheckResult Success(
            string latestSha,
            RepositoryComparison comparison,
            int? commitsBehind,
            int? commitsAhead,
            string? latestVersion = null,
            string? releaseUrl = null) =>
            new(
                true,
                latestSha,
                comparison,
                commitsBehind,
                commitsAhead,
                UpdateCheckUnavailableReason.None,
                null,
                latestVersion,
                releaseUrl);

        public static RepositoryUpdateCheckResult Failure(UpdateCheckUnavailableReason reason, string? errorMessage) =>
            new(false, null, RepositoryComparison.Unknown, null, null, reason, errorMessage, null, null);
    }

    public interface IRepositoryUpdateChecker
    {
        Task<RepositoryUpdateCheckResult> CheckAsync(
            string repository,
            string? builtSha,
            CancellationToken cancellationToken);
    }
}
