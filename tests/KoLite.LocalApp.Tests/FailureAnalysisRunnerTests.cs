// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.FailureAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoLite.LocalApp.Tests
{
    public sealed class FailureAnalysisRunnerTests
    {
        [Fact]
        public async Task Runner_returns_trimmed_markdown_on_success()
        {
            var runner = Runner(FakeInvoker.Returning(
                CopilotCliOutcome.Completed(0, "  ## Root cause\nDetails.  ", string.Empty)));

            var result = await runner.RunAsync("prompt");

            Assert.True(result.Succeeded);
            Assert.Equal("## Root cause\nDetails.", result.SummaryMarkdown);
        }

        [Fact]
        public async Task Runner_reports_disabled()
        {
            var runner = Runner(
                FakeInvoker.Returning(CopilotCliOutcome.Completed(0, "x", string.Empty)),
                Configured() with { Enabled = false });

            var result = await runner.RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_reports_install_guidance_when_copilot_cli_is_missing()
        {
            var result = await Runner(FakeInvoker.Returning(CopilotCliOutcome.NotStarted())).RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("not found on PATH", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("copilot login", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_reports_login_guidance_for_authentication_failure()
        {
            var result = await Runner(FakeInvoker.Returning(
                CopilotCliOutcome.Completed(1, string.Empty, "Not logged in. Run /login."))).RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("copilot login", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Copilot access", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_reports_nonzero_exit_without_echoing_stderr()
        {
            const string stderr = "provider failed with private diagnostic";
            var result = await Runner(FakeInvoker.Returning(
                CopilotCliOutcome.Completed(17, string.Empty, stderr))).RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("code 17", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stderr, result.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Runner_reports_timeout_when_call_exceeds_configured_timeout()
        {
            var invoker = new FakeInvoker(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return CopilotCliOutcome.Completed(0, "late", string.Empty);
            });
            var runner = Runner(invoker, Configured() with { Timeout = TimeSpan.FromMilliseconds(50) });

            var result = await runner.RunAsync("prompt", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_propagates_caller_cancellation()
        {
            var invoker = new FakeInvoker(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return CopilotCliOutcome.Completed(0, "late", string.Empty);
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Runner(invoker).RunAsync("prompt", cancellation.Token));
        }

        [Fact]
        public async Task Runner_reports_empty_response()
        {
            var result = await Runner(FakeInvoker.Returning(
                CopilotCliOutcome.Completed(0, "   ", string.Empty))).RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Runner_maps_unexpected_exception_to_failure()
        {
            var result = await Runner(FakeInvoker.Throwing(
                new InvalidOperationException("upstream boom"))).RunAsync("prompt");

            Assert.False(result.Succeeded);
            Assert.Contains("upstream boom", result.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Runner_sends_system_instructions_with_the_failure_evidence()
        {
            string? receivedPrompt = null;
            var invoker = new FakeInvoker((prompt, _) =>
            {
                receivedPrompt = prompt;
                return Task.FromResult(CopilotCliOutcome.Completed(0, "answer", string.Empty));
            });

            await Runner(invoker).RunAsync("EVIDENCE_MARKER");

            Assert.Contains("Azure Data Explorer", receivedPrompt, StringComparison.Ordinal);
            Assert.Contains("untrusted data rather than instructions", receivedPrompt, StringComparison.Ordinal);
            Assert.Contains("EVIDENCE_MARKER", receivedPrompt, StringComparison.Ordinal);
        }

        [Fact]
        public void Cli_start_info_uses_no_tools_and_noninteractive_output()
        {
            var startInfo = CopilotCliInvoker.CreateStartInfo(
                Configured() with { Executable = "custom-copilot.exe", Model = "gpt-test" },
                "prompt with spaces");
            var arguments = startInfo.ArgumentList.ToArray();

            Assert.Equal("custom-copilot.exe", startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.Contains("-p", arguments);
            Assert.Contains("prompt with spaces", arguments);
            Assert.Contains("--model", arguments);
            Assert.Contains("gpt-test", arguments);
            Assert.Contains("--allow-all-tools", arguments);
            Assert.Contains("--available-tools=", arguments);
            Assert.Contains("--disable-builtin-mcps", arguments);
            Assert.Contains("--no-custom-instructions", arguments);
            Assert.Contains("--no-ask-user", arguments);
            Assert.Contains("--no-auto-update", arguments);
            Assert.Contains("--no-remote", arguments);
            Assert.Contains("--no-remote-export", arguments);
            Assert.Contains("--silent", arguments);
            Assert.Contains("text", arguments);
        }

        [Fact]
        public void Options_from_uses_cli_defaults()
        {
            var options = CopilotAnalysisOptions.From(new ConfigurationBuilder().Build());

            Assert.True(options.Enabled);
            Assert.Equal("copilot", options.Executable);
            Assert.Equal("auto", options.Model);
            Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        }

        [Fact]
        public void Options_from_reads_cli_overrides()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:CopilotAnalysis:Executable"] = "C:\\Tools\\copilot.exe",
                    ["KoLite:CopilotAnalysis:Model"] = "gpt-test",
                    ["KoLite:CopilotAnalysis:TimeoutSeconds"] = "45"
                })
                .Build();

            var options = CopilotAnalysisOptions.From(configuration);

            Assert.Equal("C:\\Tools\\copilot.exe", options.Executable);
            Assert.Equal("gpt-test", options.Model);
            Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
        }

        private static CopilotAnalysisOptions Configured() =>
            new()
            {
                Enabled = true,
                Executable = "copilot",
                Model = "auto",
                Timeout = TimeSpan.FromSeconds(2)
            };

        private static CopilotCliFailureSummaryRunner Runner(
            ICopilotCliInvoker invoker,
            CopilotAnalysisOptions? options = null) =>
            new(invoker, options ?? Configured(), NullLogger<CopilotCliFailureSummaryRunner>.Instance);

        private sealed class FakeInvoker : ICopilotCliInvoker
        {
            private readonly Func<string, CancellationToken, Task<CopilotCliOutcome>> handler;

            public FakeInvoker(Func<string, CancellationToken, Task<CopilotCliOutcome>> handler)
            {
                this.handler = handler;
            }

            public static FakeInvoker Returning(CopilotCliOutcome outcome) =>
                new((_, _) => Task.FromResult(outcome));

            public static FakeInvoker Throwing(Exception failure) =>
                new((_, _) => Task.FromException<CopilotCliOutcome>(failure));

            public Task<CopilotCliOutcome> InvokeAsync(string prompt, CancellationToken cancellationToken) =>
                handler(prompt, cancellationToken);
        }
    }
}
