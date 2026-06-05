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
        string? ErrorMessage)
    {
        public static RepositoryUpdateCheckResult Success(
            string latestSha,
            RepositoryComparison comparison,
            int? commitsBehind,
            int? commitsAhead) =>
            new(true, latestSha, comparison, commitsBehind, commitsAhead, UpdateCheckUnavailableReason.None, null);

        public static RepositoryUpdateCheckResult Failure(UpdateCheckUnavailableReason reason, string? errorMessage) =>
            new(false, null, RepositoryComparison.Unknown, null, null, reason, errorMessage);
    }

    public interface IRepositoryUpdateChecker
    {
        Task<RepositoryUpdateCheckResult> CheckAsync(
            string repository,
            string branch,
            string? builtSha,
            CancellationToken cancellationToken);
    }
}
