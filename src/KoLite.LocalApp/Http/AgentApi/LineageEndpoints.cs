// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Application;
using KoLite.LocalApp.Application.Lineage;
using KoLite.LocalApp.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KoLite.LocalApp.Http.AgentApi
{
    public static class LineageEndpoints
    {
        public static void Map(RouteGroupBuilder api)
        {
            api.MapPost("/dependency-graphs/kusto-lineage", GetKustoLineage)
                .WithTags("Kusto lineage")
                .WithName("GetKustoLineage")
                .WithSummary("Resolves read-only Kusto lineage for selected Kusto Slice Runner jobs.");
        }

        private static async Task<Results<Ok<KustoLineageResponse>, ProblemHttpResult>> GetKustoLineage(
            KustoLineageRequest request,
            LineageApplicationService lineage,
            CancellationToken cancellationToken)
        {
            try
            {
                if (request.JobIds is null || request.JobIds.Length == 0)
                {
                    throw new ApplicationProblemException(
                        StatusCodes.Status400BadRequest,
                        "job-selection-required",
                        "At least one job is required.",
                        "'jobIds' must contain at least one permanent job GUID.");
                }

                var jobIds = request.JobIds.Select(value =>
                {
                    if (!Guid.TryParse(value, out var parsed))
                    {
                        throw new ApplicationProblemException(
                            StatusCodes.Status400BadRequest,
                            "invalid-job-id",
                            "A job ID is invalid.",
                            $"'{value}' is not a GUID.");
                    }

                    return parsed.ToString("N");
                }).Distinct(StringComparer.Ordinal).ToArray();
                var graph = await lineage.GetKustoLineageAsync(jobIds, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new KustoLineageResponse(
                    graph.Nodes.Select(node => new KustoLineageNodeResponse(
                        node.Id,
                        node.Label,
                        node.Status,
                        node.Status.ToLowerInvariant(),
                        node.StatusText,
                        node.Kind,
                        node.Resolved,
                        node.Focal,
                        node.Href,
                        node.Counts is null
                            ? null
                            : new KustoLineageCountsResponse(
                                node.Counts.Total,
                                node.Counts.Missing,
                                node.Counts.Queued,
                                node.Counts.Running,
                                node.Counts.Completed,
                                node.Counts.Failed,
                                node.Counts.DeadLettered,
                                node.Counts.DependencyBlocked))).ToArray(),
                    graph.Edges.Select(edge => new KustoLineageEdgeResponse(
                        edge.FromId,
                        edge.ToId,
                        edge.Implicit)).ToArray(),
                    graph.Legend.Select(item => new KustoLineageLegendResponse(
                        item.Status,
                        item.Status.ToLowerInvariant(),
                        item.Label)).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }
    }
}
