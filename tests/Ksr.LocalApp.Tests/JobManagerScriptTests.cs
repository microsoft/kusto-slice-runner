// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ksr.LocalApp.Tests
{
    public sealed class JobManagerScriptTests
    {
        [Theory]
        [InlineData("""{"supportedApiVersions":["v1"],"retention":{"ingestionThrottlesDeleted":2}}""")]
        [InlineData("""{"supportedApiVersions":["v1"],"retention":{"queueRowsDeleted":1}}""")]
        public async Task Status_supports_both_pre_and_post_retirement_responses(string body)
        {
            await using var server = new FakeHttpServer(_ => Task.FromResult(FakeResponse.Json(body)));

            var result = await RunHelper(server.BaseUrl, "-Action", "System-Status");

            Assert.True(result.ExitCode == 0, result.Error);
        }

        [Fact]
        public async Task Pause_captures_etag_and_sends_if_match()
        {
            var jobId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var requests = new List<RecordedRequest>();
            await using var server = new FakeHttpServer(async request =>
            {
                requests.Add(request);
                return request.Path switch
                {
                    "/api/v1/system/status" => FakeResponse.Json(
                        """{"supportedApiVersions":["v1"]}"""),
                    "/api/v1/jobs/11111111-2222-3333-4444-555555555555" => FakeResponse.Json(
                        """{"job":{"jobId":"11111111222233334444555555555555","catalogVersion":7}}""",
                        ("ETag", "\"catalog-7\"")),
                    "/api/v1/jobs/11111111-2222-3333-4444-555555555555/actions/pause" => FakeResponse.Json(
                        """{"job":{"jobId":"11111111222233334444555555555555","lifecycleState":"paused"}}"""),
                    _ => FakeResponse.Json("""{"code":"unexpected","detail":"unexpected route"}""", statusCode: 404)
                };
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Pause",
                "-JobId", jobId.ToString("D"),
                "-Reason", "test");

            Assert.Equal(0, result.ExitCode);
            var post = Assert.Single(requests, item => item.Method == "POST");
            Assert.Equal("\"catalog-7\"", post.Headers["If-Match"]);
        }

        [Fact]
        public async Task Write_stops_before_mutation_when_v1_is_not_advertised()
        {
            var requests = new List<RecordedRequest>();
            await using var server = new FakeHttpServer(request =>
            {
                requests.Add(request);
                return Task.FromResult(FakeResponse.Json("""{"supportedApiVersions":["v2"]}"""));
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Pause",
                "-JobId", Guid.NewGuid().ToString("D"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not advertise agent API v1", result.Error, StringComparison.Ordinal);
            var request = Assert.Single(requests);
            Assert.Equal("GET", request.Method);
            Assert.Equal("/api/v1/system/status", request.Path);
        }

        [Fact]
        public async Task Write_against_a_legacy_app_fails_before_mutation()
        {
            var requests = new List<RecordedRequest>();
            await using var server = new FakeHttpServer(request =>
            {
                requests.Add(request);
                return Task.FromResult(FakeResponse.Json(
                    """{"code":"not-found","detail":"The v1 API is unavailable."}""",
                    statusCode: 404,
                    contentType: "application/problem+json"));
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Soft-Delete",
                "-JobId", Guid.NewGuid().ToString("D"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("GET /api/v1/system/status returned HTTP 404", result.Error, StringComparison.Ordinal);
            var request = Assert.Single(requests);
            Assert.Equal("GET", request.Method);
            Assert.Equal("/api/v1/system/status", request.Path);
        }

        [Fact]
        public async Task Problem_details_code_and_detail_are_reported()
        {
            await using var server = new FakeHttpServer(_ => Task.FromResult(
                FakeResponse.Json(
                    """{"status":409,"title":"Conflict","detail":"state changed","code":"test-conflict"}""",
                    statusCode: 409,
                    contentType: "application/problem+json")));

            var result = await RunHelper(server.BaseUrl, "-Action", "System-Status");

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.Error.Contains("(test-conflict)", StringComparison.Ordinal), result.Error);
            Assert.True(result.Error.Contains("state changed", StringComparison.Ordinal), result.Error);
        }

        [Theory]
        [InlineData("""{"activityId":"single"}""")]
        [InlineData("""[{"activityId":"single"}]""")]
        public async Task Import_preserves_a_single_schedule_as_an_array(string scheduleJson)
        {
            var requests = new List<RecordedRequest>();
            await using var server = new FakeHttpServer(request =>
            {
                requests.Add(request);
                return Task.FromResult(request.Path switch
                {
                    "/api/v1/system/status" => FakeResponse.Json(
                        """{"supportedApiVersions":["v1"]}"""),
                    "/api/v1/jobs/import" => FakeResponse.Json(
                        """{"created":1,"updated":0,"total":1,"items":[]}"""),
                    _ => FakeResponse.Json(
                        """{"code":"unexpected","detail":"unexpected route"}""",
                        statusCode: 404)
                });
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Import",
                "-Json", scheduleJson,
                "-SkipValidation");

            Assert.True(result.ExitCode == 0, result.Error + Environment.NewLine + result.Output);
            var post = Assert.Single(requests, request => request.Method == "POST");
            using var body = JsonDocument.Parse(post.Body);
            var schedules = body.RootElement.GetProperty("schedules");
            Assert.Equal(JsonValueKind.Array, schedules.ValueKind);
            Assert.Single(schedules.EnumerateArray());
        }

        [Fact]
        public async Task Repair_requires_the_preview_token_before_mutation()
        {
            var requests = new List<RecordedRequest>();
            await using var server = new FakeHttpServer(request =>
            {
                requests.Add(request);
                return Task.FromResult(FakeResponse.Json("""{"supportedApiVersions":["v1"]}"""));
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Repair",
                "-JobId", Guid.NewGuid().ToString("D"),
                "-From", "2026-01-01T00:00:00Z",
                "-To", "2026-01-01T00:05:00Z",
                "-Reason", "test",
                "-ExpectedSliceCount", "1");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Repair requires -PreviewToken from Preview-Repair.", result.Error, StringComparison.Ordinal);
            Assert.Single(requests);
            Assert.DoesNotContain(requests, request => request.Method == "POST");
        }

        [Fact]
        public async Task AllPages_follows_only_the_returned_cursor()
        {
            var paths = new List<string>();
            await using var server = new FakeHttpServer(request =>
            {
                paths.Add(request.PathAndQuery);
                return Task.FromResult(request.PathAndQuery.Contains("cursor=opaque-next", StringComparison.Ordinal)
                    ? FakeResponse.Json("""{"items":[{"message":"second"}],"nextCursor":null}""")
                    : FakeResponse.Json("""{"items":[{"message":"first"}],"nextCursor":"opaque-next"}"""));
            });

            var result = await RunHelper(
                server.BaseUrl,
                "-Action", "Get-Logs",
                "-AllPages");

            Assert.True(result.ExitCode == 0, result.Error + Environment.NewLine + result.Output);
            Assert.Equal(2, paths.Count);
            Assert.DoesNotContain("cursor=", paths[0], StringComparison.Ordinal);
            Assert.Contains("cursor=opaque-next", paths[1], StringComparison.Ordinal);
        }

        private static async Task<ProcessResult> RunHelper(string baseUrl, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(
                FindRepositoryRoot(),
                ".github",
                "skills",
                "ksr-job-manager",
                "scripts",
                "Invoke-KsrJobApi.ps1"));
            startInfo.ArgumentList.Add("-BaseUrl");
            startInfo.ArgumentList.Add(baseUrl);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start pwsh.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Ksr.Local.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not find repository root.");
        }

        private sealed record ProcessResult(int ExitCode, string Output, string Error);

        private sealed record RecordedRequest(
            string Method,
            string Path,
            string PathAndQuery,
            IReadOnlyDictionary<string, string> Headers,
            string Body);

        private sealed record FakeResponse(
            int StatusCode,
            string ContentType,
            string Body,
            IReadOnlyList<(string Name, string Value)> Headers)
        {
            public static FakeResponse Json(
                string body,
                (string Name, string Value)? header = null,
                int statusCode = 200,
                string contentType = "application/json")
            {
                return new FakeResponse(
                    statusCode,
                    contentType,
                    body,
                    header is null ? [] : [header.Value]);
            }
        }

        private sealed class FakeHttpServer : IAsyncDisposable
        {
            private readonly TcpListener listener;
            private readonly Func<RecordedRequest, Task<FakeResponse>> handler;
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task loop;

            public FakeHttpServer(Func<RecordedRequest, Task<FakeResponse>> handler)
            {
                this.handler = handler;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
                loop = AcceptLoop();
            }

            public string BaseUrl { get; }

            public async ValueTask DisposeAsync()
            {
                cancellation.Cancel();
                listener.Stop();
                try
                {
                    await loop;
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException)
                {
                }

                cancellation.Dispose();
            }

            private async Task AcceptLoop()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    _ = Task.Run(() => Handle(client), cancellation.Token);
                }
            }

            private async Task Handle(TcpClient client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                var parts = requestLine.Split(' ', 3);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator]] = line[(separator + 1)..].Trim();
                    }
                }

                var body = string.Empty;
                if (headers.TryGetValue("Content-Length", out var rawLength)
                    && int.TryParse(rawLength, out var length)
                    && length > 0)
                {
                    var buffer = new char[length];
                    _ = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                    body = new string(buffer);
                }

                var target = parts.Length > 1 ? parts[1] : "/";
                var path = target.Split('?', 2)[0];
                var request = new RecordedRequest(
                    parts.Length > 0 ? parts[0] : string.Empty,
                    path,
                    target,
                    headers,
                    body);
                var response = await handler(request);
                var responseBytes = Encoding.UTF8.GetBytes(response.Body);
                var reason = response.StatusCode switch
                {
                    200 => "OK",
                    409 => "Conflict",
                    404 => "Not Found",
                    _ => "Response"
                };
                var builder = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
                    .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
                    .Append("Content-Length: ").Append(responseBytes.Length).Append("\r\n")
                    .Append("Connection: close\r\n");
                foreach (var header in response.Headers)
                {
                    builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
                }
                builder.Append("\r\n");
                var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
                await stream.WriteAsync(headerBytes);
                await stream.WriteAsync(responseBytes);
                await stream.FlushAsync();
                client.Dispose();
            }
        }
    }
}
