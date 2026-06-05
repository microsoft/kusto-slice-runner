using System.Globalization;
using KoLite.Local.Core.Time;

namespace KoLite.LocalApp.Updates
{
    public sealed record UpdateBadgeViewModel(
        UpdateCheckStatus Status,
        string StatusKey,
        string Label,
        string Title,
        bool ShowLink,
        string LinkUrl,
        string Repository,
        string Branch,
        string BuiltShaShort,
        string RemoteShaShort,
        int? CommitsBehind,
        string LastCheckedText,
        IReadOnlyList<string> DetailLines,
        string? RemediationTitle,
        IReadOnlyList<string> RemediationSteps)
    {
        public bool HasRemediation => RemediationSteps.Count > 0;
    }

    public sealed class UpdateBadgeReadModel
    {
        private readonly UpdateCheckRuntimeState runtimeState;
        private readonly LocalUpdateCheckOptions options;
        private readonly IClock clock;

        public UpdateBadgeReadModel(UpdateCheckRuntimeState runtimeState, LocalUpdateCheckOptions options, IClock clock)
        {
            this.runtimeState = runtimeState;
            this.options = options;
            this.clock = clock;
        }

        public UpdateBadgeViewModel Get()
        {
            var snapshot = runtimeState.GetSnapshot();
            var repository = options.Repository;
            var branch = options.Branch;
            var builtShort = BuildInfo.ShortSha(snapshot.BuiltSha);
            var remoteShort = BuildInfo.ShortSha(snapshot.RemoteSha);
            var repoUrl = $"https://github.com/{repository}";

            var (statusKey, label, title) = snapshot.Status switch
            {
                UpdateCheckStatus.UpToDate => ("uptodate", "Up to date", $"KO Lite is up to date with {repository} ({branch})."),
                UpdateCheckStatus.UpdateAvailable => ("update", BuildUpdateLabel(snapshot.CommitsBehind), $"A newer version of KO Lite is available on {repository} ({branch})."),
                UpdateCheckStatus.Ahead => ("ahead", BuildAheadLabel(snapshot.CommitsAhead), $"This build is ahead of {repository} ({branch})."),
                UpdateCheckStatus.Diverged => ("diverged", BuildDivergedLabel(snapshot.CommitsAhead, snapshot.CommitsBehind), $"This build has diverged from {repository} ({branch})."),
                UpdateCheckStatus.Checking => ("checking", "Checking\u2026", "Checking for updates\u2026"),
                _ => ("unavailable", "Updates: unavailable", "Update checks are unavailable.")
            };

            var showLink = snapshot.Status == UpdateCheckStatus.UpdateAvailable;
            var linkUrl = showLink && !string.IsNullOrWhiteSpace(snapshot.BuiltSha)
                ? $"{repoUrl}/compare/{snapshot.BuiltSha}...{branch}"
                : $"{repoUrl}/commits/{branch}";

            var detailLines = BuildDetailLines(snapshot, repository, branch, builtShort, remoteShort);
            var (remediationTitle, remediationSteps) = BuildRemediation(snapshot, repository);

            return new UpdateBadgeViewModel(
                snapshot.Status,
                statusKey,
                label,
                title,
                showLink,
                linkUrl,
                repository,
                branch,
                builtShort,
                remoteShort,
                snapshot.CommitsBehind,
                FormatLastChecked(snapshot.LastCheckedUtc),
                detailLines,
                remediationTitle,
                remediationSteps);
        }

        private static string BuildUpdateLabel(int? commitsBehind)
        {
            if (commitsBehind is > 0)
            {
                return commitsBehind == 1 ? "Update available (1 behind)" : $"Update available ({commitsBehind} behind)";
            }

            return "Update available";
        }

        private static string BuildAheadLabel(int? commitsAhead)
        {
            if (commitsAhead is > 0)
            {
                return commitsAhead == 1 ? "Ahead of published (1 ahead)" : $"Ahead of published ({commitsAhead} ahead)";
            }

            return "Ahead of published";
        }

        private static string BuildDivergedLabel(int? commitsAhead, int? commitsBehind)
        {
            if (commitsAhead is > 0 && commitsBehind is > 0)
            {
                return $"Diverged ({commitsAhead} ahead / {commitsBehind} behind)";
            }

            return "Diverged";
        }

        private IReadOnlyList<string> BuildDetailLines(
            UpdateCheckSnapshot snapshot,
            string repository,
            string branch,
            string builtShort,
            string remoteShort)
        {
            var lines = new List<string>
            {
                $"Repository: {repository} ({branch})",
                $"This build: {(string.IsNullOrEmpty(builtShort) ? "unknown" : builtShort)}"
            };

            if (!string.IsNullOrEmpty(remoteShort))
            {
                lines.Add($"Latest remote: {remoteShort}");
            }

            if (snapshot.CommitsBehind is > 0)
            {
                lines.Add(snapshot.CommitsBehind == 1
                    ? "1 commit behind"
                    : $"{snapshot.CommitsBehind} commits behind");
            }

            if (snapshot.CommitsAhead is > 0)
            {
                lines.Add(snapshot.CommitsAhead == 1
                    ? "1 commit ahead"
                    : $"{snapshot.CommitsAhead} commits ahead");
            }

            if (snapshot.Status == UpdateCheckStatus.Ahead && snapshot.CommitsAhead is null)
            {
                lines.Add("This build isn't on the published branch yet (likely unpushed).");
            }

            lines.Add($"Last checked: {FormatLastChecked(snapshot.LastCheckedUtc)}");
            return lines;
        }

        private (string? Title, IReadOnlyList<string> Steps) BuildRemediation(UpdateCheckSnapshot snapshot, string repository)
        {
            if (snapshot.Status != UpdateCheckStatus.Unavailable)
            {
                return (null, Array.Empty<string>());
            }

            var steps = snapshot.Reason switch
            {
                UpdateCheckUnavailableReason.Disabled => new List<string>
                {
                    "Update checks are turned off (KoLite:UpdateCheck:Enabled = false)."
                },
                UpdateCheckUnavailableReason.NoBuildSha => new List<string>
                {
                    "This build wasn't stamped with a git commit, so it can't be compared.",
                    "Build KO Lite from a git checkout to enable update checks."
                },
                UpdateCheckUnavailableReason.GhMissing => new List<string>
                {
                    "Install the GitHub CLI (gh) from https://cli.github.com.",
                    "Restart KO Lite so it can reach GitHub."
                },
                UpdateCheckUnavailableReason.GhNotAuthenticated => new List<string>
                {
                    "Run 'gh auth login' and sign in to github.com.",
                    "KO Lite retries automatically on the next scheduled check."
                },
                UpdateCheckUnavailableReason.RepoAccessDenied => new List<string>
                {
                    $"Your signed-in GitHub account can't read {repository}.",
                    "Request access to the repository, then re-run 'gh auth login' if needed."
                },
                UpdateCheckUnavailableReason.NetworkError => new List<string>
                {
                    "Couldn't reach GitHub.",
                    "Check your network connection; KO Lite will retry automatically."
                },
                _ => new List<string>
                {
                    "Couldn't check for updates."
                }
            };

            if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage) &&
                snapshot.Reason is UpdateCheckUnavailableReason.NetworkError
                    or UpdateCheckUnavailableReason.RepoAccessDenied
                    or UpdateCheckUnavailableReason.Unknown)
            {
                steps.Add($"Details: {snapshot.ErrorMessage}");
            }

            return ("Update checks unavailable", steps);
        }

        private string FormatLastChecked(DateTimeOffset? lastCheckedUtc)
        {
            if (lastCheckedUtc is null) return "not yet";

            var elapsed = clock.UtcNow - lastCheckedUtc.Value;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
            if (elapsed < TimeSpan.FromHours(1))
            {
                var minutes = (int)elapsed.TotalMinutes;
                return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
            }

            if (elapsed < TimeSpan.FromDays(1))
            {
                var hours = (int)elapsed.TotalHours;
                return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
            }

            return lastCheckedUtc.Value.ToString("u", CultureInfo.InvariantCulture);
        }
    }
}
