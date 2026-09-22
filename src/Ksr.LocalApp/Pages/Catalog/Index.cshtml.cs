// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ksr.LocalApp.Pages.Catalog
{
    public sealed class IndexModel : PageModel
    {
        private readonly DashboardPageQuery query;

        public IndexModel(DashboardPageQuery query)
        {
            this.query = query;
        }

        public DashboardPageData Data { get; private set; } = null!;

        public void OnGet([FromQuery(Name = "tag")] string[]? tags) => Data = query.Get(TimeSpan.FromDays(1), tags);
    }
}
