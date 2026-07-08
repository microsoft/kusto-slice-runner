using System.ClientModel;
using KoLite.Local.Core.FailureSummaries;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.FailureAnalysis
{
    // The real IFailureSummaryRunner: sends the caller-built failure evidence prompt to the configured
    // model and returns its markdown analysis. It is deliberately transport-only - the evidence
    // gathering, secret sanitizing, and prompt shaping happen upstream in the analyzer, so this class
    // just adds the system persona, enforces a timeout, and maps failures to friendly messages.
    public sealed class ChatClientFailureSummaryRunner : IFailureSummaryRunner
    {
        private const string SystemPrompt =
            "You are an expert Azure Data Explorer (Kusto) operations engineer helping analyze failures of a " +
            "scheduled data-production job in KO Lite, a local-first Kusto orchestrator. You will be given evidence " +
            "gathered from KO Lite's local state: the job definition, slice-state counts, and recent failed or " +
            "dead-lettered slices with their Kusto error codes and messages, plus recent attempts. Produce a concise, " +
            "high-signal root-cause analysis in GitHub-flavored Markdown covering: (1) a one-line verdict; (2) what " +
            "failed and the blast radius (how many slices and the affected time window); (3) the most likely root " +
            "cause, grounded in the specific error text and clusters seen; (4) whether it looks transient/retryable " +
            "or permanent; and (5) concrete recommended next actions for an operator (for example: rerun after an " +
            "upstream cluster recovers, raise the query timeout, or fix the query). Reference the actual error codes " +
            "and cluster names from the evidence. Do not invent facts that the evidence does not support, and do not " +
            "echo secrets. Keep it under about 400 words, using short bold-labeled sections rather than a wall of text.";

        private readonly ICopilotAnalysisChatClientFactory clientFactory;
        private readonly CopilotAnalysisOptions options;
        private readonly ILogger<ChatClientFailureSummaryRunner> logger;

        public ChatClientFailureSummaryRunner(
            ICopilotAnalysisChatClientFactory clientFactory,
            CopilotAnalysisOptions options,
            ILogger<ChatClientFailureSummaryRunner> logger)
        {
            this.clientFactory = clientFactory;
            this.options = options;
            this.logger = logger;
        }

        public async Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (!options.Enabled)
            {
                return FailureSummaryRunnerResult.Failure(
                    "Copilot analysis is disabled. Set KoLite:CopilotAnalysis:Enabled to true to use it.");
            }

            try
            {
                var client = await clientFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, SystemPrompt),
                    new(ChatRole.User, prompt)
                };

                var chatOptions = new ChatOptions { ModelId = options.Model };
                if (options.MaxOutputTokens is int maxOutputTokens)
                {
                    chatOptions.MaxOutputTokens = maxOutputTokens;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(options.Timeout);

                var response = await client.GetResponseAsync(messages, chatOptions, timeoutCts.Token).ConfigureAwait(false);
                var markdown = response.Text.Trim();
                return string.IsNullOrWhiteSpace(markdown)
                    ? FailureSummaryRunnerResult.Failure("Copilot returned an empty response.")
                    : FailureSummaryRunnerResult.Success(markdown);
            }
            catch (CopilotAnalysisAuthException authError)
            {
                // No GitHub token available (for example gh is not signed in) - surface actionable
                // guidance rather than treating it as a model failure.
                return FailureSummaryRunnerResult.Failure(authError.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A caller-driven cancellation (e.g. app shutdown) is not a model failure - let it propagate.
                throw;
            }
            catch (OperationCanceledException)
            {
                return FailureSummaryRunnerResult.Failure(
                    $"Copilot analysis timed out after {options.Timeout.TotalSeconds:F0} seconds.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Copilot failure analysis call failed.");
                return FailureSummaryRunnerResult.Failure(Describe(ex));
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is ClientResultException clientError)
            {
                return clientError.Status switch
                {
                    401 or 403 => $"The GitHub Models endpoint rejected the request (HTTP {clientError.Status}). " +
                        "Your GitHub CLI sign-in may not have access to GitHub Models - ensure GitHub Models is " +
                        "enabled for your account or organization.",
                    404 => "The configured model was not found at the endpoint (HTTP 404). " +
                        "Check KoLite:CopilotAnalysis:Model and KoLite:CopilotAnalysis:Endpoint.",
                    429 => "The model endpoint rate-limited the request (HTTP 429). Try again shortly.",
                    _ => $"The model endpoint returned an error (HTTP {clientError.Status})."
                };
            }

            return $"Copilot analysis failed: {ex.Message}";
        }
    }
}
