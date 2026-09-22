// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Rerun;
using Ksr.Local.Sqlite.Rerun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ksr.LocalApp.Pages.Reruns
{
    public sealed class DetailsModel : PageModel
    {
        private readonly SqliteRerunService reruns;

        public DetailsModel(SqliteRerunService reruns)
        {
            this.reruns = reruns;
        }

        public RerunBatchReadout? Batch { get; private set; }

        public IActionResult OnGet(string rerunBatchId)
        {
            Batch = reruns.GetBatch(rerunBatchId);
            if (Batch is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Page();
        }
    }
}
