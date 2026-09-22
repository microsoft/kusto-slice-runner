// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
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
