using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Updates
{
    public sealed class LocalUpdateCheckBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalUpdateCheckOptions options;
        private readonly UpdateCheckRuntimeState runtimeState;
        private readonly IRepositoryUpdateChecker checker;
        private readonly AppBuildVersion buildVersion;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly IClock clock;
        private readonly ILogger<LocalUpdateCheckBackgroundService> logger;

        public LocalUpdateCheckBackgroundService(
            IServiceScopeFactory scopes,
            LocalUpdateCheckOptions options,
            UpdateCheckRuntimeState runtimeState,
            IRepositoryUpdateChecker checker,
            AppBuildVersion buildVersion,
            LocalShutdownDrainCoordinator shutdownDrain,
            IClock clock,
            ILogger<LocalUpdateCheckBackgroundService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.runtimeState = runtimeState;
            this.checker = checker;
            this.buildVersion = buildVersion;
            this.shutdownDrain = shutdownDrain;
            this.clock = clock;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled || string.IsNullOrWhiteSpace(buildVersion.CommitSha))
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!shutdownDrain.IsDrainRequested)
                {
                    await RunCheckAsync(stoppingToken).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task RunCheckAsync(CancellationToken cancellationToken)
        {
            RepositoryUpdateCheckResult result;
            try
            {
                result = await checker
                    .CheckAsync(options.Repository, options.Branch, buildVersion.CommitSha, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Update check failed unexpectedly.");
                result = RepositoryUpdateCheckResult.Failure(UpdateCheckUnavailableReason.Unknown, ex.Message);
            }

            var previous = runtimeState.GetSnapshot();
            var next = BuildSnapshot(result);
            runtimeState.Update(next);

            if (previous.Status != next.Status || previous.Reason != next.Reason)
            {
                RecordTransition(next);
            }
        }

        private UpdateCheckSnapshot BuildSnapshot(RepositoryUpdateCheckResult result)
        {
            var nowUtc = clock.UtcNow;
            var builtSha = buildVersion.CommitSha;

            if (!result.Succeeded)
            {
                return new UpdateCheckSnapshot(
                    UpdateCheckStatus.Unavailable,
                    result.FailureReason == UpdateCheckUnavailableReason.None
                        ? UpdateCheckUnavailableReason.Unknown
                        : result.FailureReason,
                    builtSha,
                    RemoteSha: null,
                    CommitsBehind: null,
                    CommitsAhead: null,
                    LastCheckedUtc: nowUtc,
                    ErrorMessage: result.ErrorMessage);
            }

            var status = result.Comparison switch
            {
                RepositoryComparison.Identical => UpdateCheckStatus.UpToDate,
                RepositoryComparison.RemoteAhead => UpdateCheckStatus.UpdateAvailable,
                RepositoryComparison.LocalAhead => UpdateCheckStatus.Ahead,
                RepositoryComparison.Diverged => UpdateCheckStatus.Diverged,
                // Direction is unknown but the SHAs differ: assume a newer remote is available.
                _ => UpdateCheckStatus.UpdateAvailable
            };

            return new UpdateCheckSnapshot(
                status,
                UpdateCheckUnavailableReason.None,
                builtSha,
                result.LatestSha,
                CommitsBehind: status == UpdateCheckStatus.UpToDate ? null : result.CommitsBehind,
                CommitsAhead: result.CommitsAhead,
                LastCheckedUtc: nowUtc,
                ErrorMessage: null);
        }

        private void RecordTransition(UpdateCheckSnapshot snapshot)
        {
            logger.LogInformation(
                "Update check status is now {Status} (reason {Reason}). Built {BuiltSha}, remote {RemoteSha}.",
                snapshot.Status,
                snapshot.Reason,
                BuildInfo.ShortSha(snapshot.BuiltSha),
                BuildInfo.ShortSha(snapshot.RemoteSha));

            try
            {
                using var scope = scopes.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
                var propertiesJson = JsonSerializer.Serialize(new
                {
                    status = snapshot.Status.ToString(),
                    reason = snapshot.Reason.ToString(),
                    repository = options.Repository,
                    branch = options.Branch,
                    builtSha = snapshot.BuiltSha,
                    remoteSha = snapshot.RemoteSha,
                    commitsBehind = snapshot.CommitsBehind,
                    commitsAhead = snapshot.CommitsAhead,
                    lastCheckedUtc = snapshot.LastCheckedUtc?.ToString("O", CultureInfo.InvariantCulture)
                });

                observability.RecordLog(
                    snapshot.Status == UpdateCheckStatus.Unavailable ? "Warning" : "Information",
                    $"Update check status changed to {snapshot.Status}.",
                    "update-check",
                    propertiesJson: propertiesJson);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to record update check transition log.");
            }
        }
    }
}
