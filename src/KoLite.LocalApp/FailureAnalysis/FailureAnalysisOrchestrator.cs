using KoLite.Local.Core.FailureSummaries;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.FailureAnalysis
{
    // Runs a prompt through the failure-summary runner on a background task and records the outcome in
    // the ephemeral registry. The prompt is a plain immutable string built earlier inside the request
    // scope, so this touches only singletons and is safe to outlive the originating HTTP request. The
    // runner enforces its own timeout; app shutdown cancels an in-flight call via ApplicationStopping.
    public sealed class FailureAnalysisOrchestrator
    {
        private readonly IFailureSummaryRunner runner;
        private readonly FailureAnalysisRunRegistry registry;
        private readonly IHostApplicationLifetime lifetime;
        private readonly ILogger<FailureAnalysisOrchestrator> logger;

        public FailureAnalysisOrchestrator(
            IFailureSummaryRunner runner,
            FailureAnalysisRunRegistry registry,
            IHostApplicationLifetime lifetime,
            ILogger<FailureAnalysisOrchestrator> logger)
        {
            this.runner = runner;
            this.registry = registry;
            this.lifetime = lifetime;
            this.logger = logger;
        }

        public void Launch(string runId, string prompt)
        {
            _ = Task.Run(() => RunAsync(runId, prompt));
        }

        private async Task RunAsync(string runId, string prompt)
        {
            try
            {
                var result = await runner.RunAsync(prompt, lifetime.ApplicationStopping).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    registry.Complete(runId, result.SummaryMarkdown);
                }
                else
                {
                    registry.Fail(runId, string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "Failure analysis did not produce a result."
                        : result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                registry.Fail(runId, "Failure analysis was canceled because the app is shutting down.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexpected error while running Copilot failure analysis {RunId}.", runId);
                registry.Fail(runId, "An unexpected error occurred while running the analysis.");
            }
        }
    }
}
