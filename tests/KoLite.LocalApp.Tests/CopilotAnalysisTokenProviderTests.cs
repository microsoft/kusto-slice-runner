using KoLite.LocalApp.FailureAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoLite.LocalApp.Tests
{
    public sealed class CopilotAnalysisTokenProviderTests
    {
        [Fact]
        public void Interpret_returns_install_guidance_when_gh_is_not_started()
        {
            var result = GhCliCopilotAnalysisTokenProvider.Interpret(GhAuthTokenOutcome.NotStarted());

            Assert.False(result.HasToken);
            Assert.Contains("gh auth login", result.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cli.github.com", result.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Interpret_returns_trimmed_token_on_success()
        {
            var result = GhCliCopilotAnalysisTokenProvider.Interpret(
                GhAuthTokenOutcome.Completed(0, "  gho_exampletoken\n", string.Empty));

            Assert.True(result.HasToken);
            Assert.Equal("gho_exampletoken", result.Token);
            Assert.Null(result.UnavailableMessage);
        }

        [Fact]
        public void Interpret_returns_login_guidance_on_empty_success_output()
        {
            var result = GhCliCopilotAnalysisTokenProvider.Interpret(
                GhAuthTokenOutcome.Completed(0, "   \n", string.Empty));

            Assert.False(result.HasToken);
            Assert.Contains("gh auth login", result.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Interpret_returns_login_guidance_on_nonzero_exit()
        {
            var result = GhCliCopilotAnalysisTokenProvider.Interpret(
                GhAuthTokenOutcome.Completed(1, string.Empty, "not logged in to any GitHub hosts"));

            Assert.False(result.HasToken);
            Assert.Contains("gh auth login", result.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Token_result_tostring_redacts_the_token()
        {
            var text = CopilotAnalysisTokenResult.Available("gho_super_secret").ToString();

            Assert.DoesNotContain("gho_super_secret", text);
            Assert.Contains("HasToken = True", text);
        }

        [Fact]
        public async Task GetTokenAsync_caches_a_successful_token_across_calls()
        {
            var invocations = 0;
            var provider = new GhCliCopilotAnalysisTokenProvider(
                NullLogger<GhCliCopilotAnalysisTokenProvider>.Instance,
                _ =>
                {
                    invocations++;
                    return Task.FromResult(GhAuthTokenOutcome.Completed(0, "gho_token\n", string.Empty));
                });

            var first = await provider.GetTokenAsync(CancellationToken.None);
            var second = await provider.GetTokenAsync(CancellationToken.None);

            Assert.Equal("gho_token", first.Token);
            Assert.Equal("gho_token", second.Token);
            Assert.Equal(1, invocations);
        }

        [Fact]
        public async Task GetTokenAsync_retries_after_an_unavailable_result()
        {
            var invocations = 0;
            var provider = new GhCliCopilotAnalysisTokenProvider(
                NullLogger<GhCliCopilotAnalysisTokenProvider>.Instance,
                _ =>
                {
                    invocations++;
                    return Task.FromResult(invocations == 1
                        ? GhAuthTokenOutcome.NotStarted()
                        : GhAuthTokenOutcome.Completed(0, "gho_token\n", string.Empty));
                });

            var first = await provider.GetTokenAsync(CancellationToken.None);
            var second = await provider.GetTokenAsync(CancellationToken.None);

            Assert.False(first.HasToken);
            Assert.True(second.HasToken);
            Assert.Equal(2, invocations);
        }
    }
}
