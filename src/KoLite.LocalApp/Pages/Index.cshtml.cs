// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages
{
    public sealed class IndexModel : PageModel
    {
        private readonly DashboardPageQuery query;

        public IndexModel(DashboardPageQuery query)
        {
            this.query = query;
        }

        public DashboardPageData Data { get; private set; } = null!;
        public string Range { get; private set; } = "1d";
        public DashboardSort Sort { get; private set; } = DashboardSort.Default;
        public IReadOnlyList<ChartRangeLink> RangeLinks => ChartRangeOptions.Links;

        public string? BulkOperationSummary => TempData[Catalog.CatalogBulkOperations.TempDataKey] as string;

        public void OnGet(string? range, [FromQuery(Name = "tag")] string[]? tags, string? sort, string? dir)
        {
            Range = ChartRangeOptions.Normalize(range);
            Sort = DashboardSort.Parse(sort, dir);
            Data = query.Get(ChartRangeOptions.Parse(Range), tags, Sort);
        }
    }
}
