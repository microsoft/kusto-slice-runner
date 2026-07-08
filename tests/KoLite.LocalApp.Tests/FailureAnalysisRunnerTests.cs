using KoLite.LocalApp.FailureAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoLite.LocalApp.Tests
{
    public sealed class FailureAnalysisRunnerTests
    {
        [Fact]
        public async Task Runner_returns_trimmed_markdown_on_success()
        {
            var client = new FakeChatClient(_ => Task.FromResult(Response("  ## Root cause\nDetails.  ")));
            var runner = Runner(Configured(), FakeFactory.Returning(client));

            var result = await runner.RunAsync("prompt");

            Assert.True(result.Succeeded);
            Assert.Equal("## Root cause\nDetails.", result.SummaryMarkdown);
        }

        [Fact]
        public async Task Runner_reports_disabled()
        {
            var runner = Runner(
                Configured() with { Enabled = false },
                FakeFactory.Returning(new FakeChatClient(_ => Task.FromResult(Response("x")))));

            var result = await runner.RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_reports_auth_unavailable_guidance_when_no_github_token()
        {
            var runner = Runner(Configured(), FakeFactory.Throwing(
                new CopilotAnalysisAuthException("Run `gh auth login` to sign in, then try again.")));

            var result = await runner.RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("gh auth login", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_maps_unexpected_exception_to_failure()
        {
            var client = new FakeChatClient(_ => throw new InvalidOperationException("upstream boom"));
            var runner = Runner(Configured(), FakeFactory.Returning(client));

            var result = await runner.RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("upstream boom", result.ErrorMessage);
        }

        [Fact]
        public async Task Runner_reports_timeout_when_call_exceeds_configured_timeout()
        {
            var client = new FakeChatClient(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Response("late");
            });
            var runner = Runner(Configured() with { Timeout = TimeSpan.FromMilliseconds(50) }, FakeFactory.Returning(client));

            var result = await runner.RunAsync("prompt", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_reports_empty_response()
        {
            var runner = Runner(Configured(), FakeFactory.Returning(new FakeChatClient(_ => Task.FromResult(Response("   ")))));

            var result = await runner.RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Options_from_uses_env_independent_defaults()
        {
            var options = CopilotAnalysisOptions.From(new ConfigurationBuilder().Build());

            Assert.True(options.Enabled);
            Assert.Equal(CopilotAnalysisOptions.DefaultModel, options.Model);
            Assert.Equal(new Uri(CopilotAnalysisOptions.DefaultEndpoint), options.Endpoint);
            Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        }

        private static CopilotAnalysisOptions Configured() =>
            new() { Enabled = true, Model = "openai/gpt-4.1", Endpoint = new Uri("https://models.invalid/inference") };

        private static ChatClientFailureSummaryRunner Runner(CopilotAnalysisOptions options, ICopilotAnalysisChatClientFactory factory) =>
            new(factory, options, NullLogger<ChatClientFailureSummaryRunner>.Instance);

        private static ChatResponse Response(string text) => new(new ChatMessage(ChatRole.Assistant, text));

        private sealed class FakeFactory : ICopilotAnalysisChatClientFactory
        {
            private readonly IChatClient? client;
            private readonly Exception? failure;

            private FakeFactory(IChatClient? client, Exception? failure)
            {
                this.client = client;
                this.failure = failure;
            }

            public static FakeFactory Returning(IChatClient client) => new(client, null);
            public static FakeFactory Throwing(Exception failure) => new(null, failure);

            public Task<IChatClient> CreateAsync(CancellationToken cancellationToken)
            {
                if (failure is not null) throw failure;
                return Task.FromResult(client!);
            }
        }

        private sealed class FakeChatClient : IChatClient
        {
            private readonly Func<CancellationToken, Task<ChatResponse>> handler;
            public FakeChatClient(Func<CancellationToken, Task<ChatResponse>> handler) => this.handler = handler;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                handler(cancellationToken);

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
