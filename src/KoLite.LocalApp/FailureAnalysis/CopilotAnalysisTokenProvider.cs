using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.FailureAnalysis
{
    // Result of resolving a GitHub token for failure analysis: either a usable token, or a
    // human-readable reason it is unavailable (surfaced to the operator so they know what to fix, for
    // example "run gh auth login"). The token itself is never logged.
    public sealed record CopilotAnalysisTokenResult
    {
        private CopilotAnalysisTokenResult(string? token, string? unavailableMessage)
        {
            Token = token;
            UnavailableMessage = unavailableMessage;
        }

        public string? Token { get; }
        public string? UnavailableMessage { get; }
        public bool HasToken => !string.IsNullOrWhiteSpace(Token);

        public static CopilotAnalysisTokenResult Available(string token) => new(token, null);
        public static CopilotAnalysisTokenResult Unavailable(string message) => new(null, message);

        // Redact the token; expose only its presence and any unavailable reason.
        public override string ToString() =>
            $"CopilotAnalysisTokenResult {{ HasToken = {HasToken}, Unavailable = {UnavailableMessage} }}";
    }

    // Resolves the GitHub token used to authenticate the failure-analysis model call. Kept behind an
    // interface so the chat-client factory is testable without a real credential source.
    public interface ICopilotAnalysisTokenProvider
    {
        Task<CopilotAnalysisTokenResult> GetTokenAsync(CancellationToken cancellationToken);
    }

    // Thrown when no GitHub token can be obtained. Carries the operator-facing guidance message so the
    // runner can surface it verbatim instead of a generic model error.
    public sealed class CopilotAnalysisAuthException : Exception
    {
        public CopilotAnalysisAuthException(string message) : base(message)
        {
        }
    }

    // Raw outcome of invoking "gh auth token", kept separate from interpretation so the classifier is a
    // pure, unit-testable function (mirrors GhCliRepositoryUpdateChecker's static helpers).
    public sealed record GhAuthTokenOutcome
    {
        private GhAuthTokenOutcome(bool started, int exitCode, string standardOutput, string standardError)
        {
            Started = started;
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public bool Started { get; }
        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }

        // The gh executable could not be launched (for example it is not installed / not on PATH).
        public static GhAuthTokenOutcome NotStarted() => new(false, -1, string.Empty, string.Empty);

        public static GhAuthTokenOutcome Completed(int exitCode, string standardOutput, string standardError) =>
            new(true, exitCode, standardOutput ?? string.Empty, standardError ?? string.Empty);
    }

    // Obtains a GitHub token by shelling out to "gh auth token", reusing the operator's existing GitHub
    // CLI sign-in so no personal access token has to be created or stored. The resolved token is cached
    // briefly (it is stable between logins) to avoid launching gh on every analysis run, and refreshed on
    // expiry so a re-login or rotation is picked up. Process invocation is injected so the resolver is
    // testable without spawning gh.
    public sealed class GhCliCopilotAnalysisTokenProvider : ICopilotAnalysisTokenProvider
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private const string GhMissingGuidance =
            "Copilot analysis needs the GitHub CLI (gh) to sign in, but it was not found on PATH. Install it " +
            "from https://cli.github.com/ and run `gh auth login`, then try again.";

        private const string GhLoginGuidance =
            "Copilot analysis could not get a GitHub token from the GitHub CLI. Run `gh auth login` to sign in " +
            "(the same sign-in KO Lite uses to check for updates), then try again.";

        private readonly Func<CancellationToken, Task<GhAuthTokenOutcome>> runGhAuthToken;
        private readonly ILogger<GhCliCopilotAnalysisTokenProvider> logger;
        private readonly SemaphoreSlim gate = new(1, 1);
        private string? cachedToken;
        private DateTimeOffset cacheExpiresAt;

        public GhCliCopilotAnalysisTokenProvider(ILogger<GhCliCopilotAnalysisTokenProvider> logger)
            : this(logger, runGhAuthToken: null)
        {
        }

        // Test seam: supply a fake "gh auth token" invocation.
        public GhCliCopilotAnalysisTokenProvider(
            ILogger<GhCliCopilotAnalysisTokenProvider> logger,
            Func<CancellationToken, Task<GhAuthTokenOutcome>>? runGhAuthToken)
        {
            this.logger = logger;
            this.runGhAuthToken = runGhAuthToken ?? RunGhAuthTokenProcessAsync;
        }

        public async Task<CopilotAnalysisTokenResult> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (TryGetCachedToken(out var cached))
            {
                return CopilotAnalysisTokenResult.Available(cached);
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryGetCachedToken(out cached))
                {
                    return CopilotAnalysisTokenResult.Available(cached);
                }

                var outcome = await runGhAuthToken(cancellationToken).ConfigureAwait(false);
                var result = Interpret(outcome);
                if (result.HasToken)
                {
                    cachedToken = result.Token;
                    cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
                }
                else
                {
                    cachedToken = null;
                    cacheExpiresAt = DateTimeOffset.MinValue;
                }

                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        // Pure classification of a "gh auth token" outcome. Static + public so it can be unit-tested
        // without spawning a process.
        public static CopilotAnalysisTokenResult Interpret(GhAuthTokenOutcome outcome)
        {
            if (outcome is null || !outcome.Started)
            {
                return CopilotAnalysisTokenResult.Unavailable(GhMissingGuidance);
            }

            if (outcome.ExitCode == 0)
            {
                var token = outcome.StandardOutput.Trim();
                return string.IsNullOrWhiteSpace(token)
                    ? CopilotAnalysisTokenResult.Unavailable(GhLoginGuidance)
                    : CopilotAnalysisTokenResult.Available(token);
            }

            return CopilotAnalysisTokenResult.Unavailable(GhLoginGuidance);
        }

        private bool TryGetCachedToken(out string token)
        {
            var current = cachedToken;
            if (current is not null && DateTimeOffset.UtcNow < cacheExpiresAt)
            {
                token = current;
                return true;
            }

            token = string.Empty;
            return false;
        }

        private async Task<GhAuthTokenOutcome> RunGhAuthTokenProcessAsync(CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
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
                logger.LogDebug(ex, "GitHub CLI (gh) could not be started for Copilot analysis auth.");
                return GhAuthTokenOutcome.NotStarted();
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
                logger.LogDebug("GitHub CLI did not return a token within {Timeout}s.", ProcessTimeout.TotalSeconds);
                return GhAuthTokenOutcome.Completed(-1, string.Empty, "gh auth token timed out.");
            }

            return GhAuthTokenOutcome.Completed(process.ExitCode, stdout.ToString(), stderr.ToString());
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
    }
}
