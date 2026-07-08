using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace KoLite.LocalApp.FailureAnalysis
{
    // Builds the Microsoft.Extensions.AI IChatClient used for failure analysis. Kept behind an
    // interface so the runner is unit-testable with a fake client, and so the concrete OpenAI wiring
    // (endpoint + token + model) lives in exactly one place.
    public interface ICopilotAnalysisChatClientFactory
    {
        // Resolves the current GitHub token and returns a client bound to it. Throws
        // CopilotAnalysisAuthException when no token can be obtained (for example gh is not signed in).
        Task<IChatClient> CreateAsync(CancellationToken cancellationToken);
    }

    // Reaches any OpenAI-compatible endpoint (GitHub Models by default) via the OpenAI v2 SDK, then
    // adapts it to IChatClient. The token comes from ICopilotAnalysisTokenProvider at call time (the
    // operator's GitHub CLI sign-in), so the built client is cached and keyed by the token it was built
    // with: it is reused while the token is stable and rebuilt only when the token changes (a re-login
    // or rotation), avoiding any dependency on a specific SDK credential-mutation API.
    public sealed class OpenAiChatClientFactory : ICopilotAnalysisChatClientFactory
    {
        private readonly CopilotAnalysisOptions options;
        private readonly ICopilotAnalysisTokenProvider tokenProvider;
        private readonly object gate = new();
        private IChatClient? cached;
        private string? cachedToken;

        public OpenAiChatClientFactory(CopilotAnalysisOptions options, ICopilotAnalysisTokenProvider tokenProvider)
        {
            this.options = options;
            this.tokenProvider = tokenProvider;
        }

        public async Task<IChatClient> CreateAsync(CancellationToken cancellationToken)
        {
            var tokenResult = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResult.HasToken)
            {
                throw new CopilotAnalysisAuthException(
                    tokenResult.UnavailableMessage ?? "Copilot analysis could not obtain a GitHub token.");
            }

            var token = tokenResult.Token!;
            lock (gate)
            {
                if (cached is not null && string.Equals(cachedToken, token, StringComparison.Ordinal))
                {
                    return cached;
                }

                var clientOptions = new OpenAIClientOptions { Endpoint = options.Endpoint };
                var openAiClient = new OpenAIClient(new ApiKeyCredential(token), clientOptions);
                cached = openAiClient.GetChatClient(options.Model).AsIChatClient();
                cachedToken = token;
                return cached;
            }
        }
    }
}
