// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Application.Lineage
{
    public sealed class LineageApplicationService
    {
        private readonly DependencyGraphKustoEnricher enricher;

        public LineageApplicationService(DependencyGraphKustoEnricher enricher)
        {
            this.enricher = enricher;
        }

        public async Task<DependencyGraphViewModel> GetKustoLineageAsync(
            IReadOnlyList<string> jobIds,
            CancellationToken cancellationToken)
        {
            try
            {
                return await enricher.EnrichAsync(jobIds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                if (detail.Length > 400)
                {
                    detail = detail[..400] + "…";
                }

                throw new ApplicationProblemException(
                    StatusCodes.Status502BadGateway,
                    "kusto-lineage-unavailable",
                    "Kusto lineage is unavailable.",
                    detail,
                    innerException: ex);
            }
        }
    }
}
