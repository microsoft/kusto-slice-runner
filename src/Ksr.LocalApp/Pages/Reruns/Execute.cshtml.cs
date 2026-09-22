// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Rerun;
using Ksr.Local.Sqlite.Rerun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ksr.LocalApp.Pages.Reruns
{
    public sealed class ExecuteModel : PageModel
    {
        private readonly SqliteRerunService reruns;

        public ExecuteModel(SqliteRerunService reruns)
        {
            this.reruns = reruns;
        }

        [BindProperty(Name = "kustoCleanupAcknowledged")] public bool KustoCleanupAcknowledged { get; set; }
        public string RerunBatchId { get; private set; } = string.Empty;
        public string? ErrorMessage { get; private set; }

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost(string rerunBatchId)
        {
            RerunBatchId = rerunBatchId;
            try
            {
                reruns.Execute(new RerunExecuteRequest(rerunBatchId, "local-web", KustoCleanupAcknowledged));
                return Redirect($"/reruns/{Uri.EscapeDataString(rerunBatchId)}");
            }
            catch (InvalidOperationException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                ErrorMessage = ex.Message;
                return Page();
            }
        }
    }
}
