using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Api
{
    // Loopback-only endpoint that enriches the dependency graph with downstream Kusto consumers. It
    // is the only API surface that contacts Kusto, runs a read-only management command, and is
    // invoked explicitly by the graph's "Resolve Kusto consumers" button. Failures (auth, permission,
    // unreachable cluster, timeout) are returned as a friendly error envelope rather than a 500.
    public static class KustoConsumersApi
    {
        public sealed record KustoConsumersRequest(string[]? JobIds);

        public static void Map(IEndpointRouteBuilder api)
        {
            api.MapPost("/dependency-graph/kusto-consumers", async (
                KustoConsumersRequest? request,
                DependencyGraphKustoEnricher enricher,
                CancellationToken cancellationToken) =>
            {
                var jobIds = request?.JobIds ?? Array.Empty<string>();
                try
                {
                    var graph = await enricher.EnrichAsync(jobIds, cancellationToken).ConfigureAwait(false);
                    return Results.Json(DependencyGraphPayload.Build(graph));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = Describe(ex) }, statusCode: 502);
                }
            });
        }

        private static string Describe(Exception ex)
        {
            var message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = ex.GetType().Name;
            }

            // Keep the surfaced message compact; the client shows it inline next to the button.
            return message.Length > 400 ? message[..400] + "…" : message;
        }
    }
}
