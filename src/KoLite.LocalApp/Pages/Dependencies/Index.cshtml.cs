// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Dependencies
{
    public sealed class IndexModel : PageModel
    {
        private readonly DependencyGraphQuery query;

        public IndexModel(DependencyGraphQuery query) => this.query = query ?? throw new ArgumentNullException(nameof(query));

        // Focal job ids come in as repeated ?job= query parameters from the dashboard bulk bar.
        [BindProperty(SupportsGet = true, Name = "job")]
        public string[] JobIds { get; set; } = Array.Empty<string>();

        public DependencyGraphViewModel Graph { get; private set; } = DependencyGraphViewModel.Empty;

        public int RequestedCount { get; private set; }

        public void OnGet()
        {
            var focal = (JobIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            RequestedCount = focal.Length;
            Graph = query.Build(focal);
        }
    }
}
