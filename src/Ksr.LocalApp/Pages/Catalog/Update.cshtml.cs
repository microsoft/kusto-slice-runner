// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Catalog;
using Ksr.LocalApp.Application;
using Ksr.LocalApp.Application.Jobs;
using Ksr.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ksr.LocalApp.Pages.Catalog
{
    public sealed class UpdateModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly JobApplicationService jobs;

        public UpdateModel(SqliteJobCatalogRepository catalog, JobApplicationService jobs)
        {
            this.catalog = catalog;
            this.jobs = jobs;
        }

        [BindProperty] public ScheduleFormInput Input { get; set; } = ScheduleFormInput.Default();
        [BindProperty(Name = "scheduleJson")] public string ScheduleJson { get; set; } = SampleScheduleFactory.CreateJson();
        [BindProperty] public string FormMode { get; set; } = "fields";
        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        public string JobId { get; private set; } = string.Empty;
        public string? ErrorMessage { get; private set; }
        public string? CatalogConflictMessage { get; private set; }
        public ScheduleEditorViewModel? Editor { get; private set; }

        public IActionResult OnGet(string jobId)
        {
            CatalogConflictMessage = CatalogConflictFeedback.Read(TempData);

            var record = catalog.Get(jobId);
            if (record is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            JobId = record.JobId;
            ExpectedVersion = record.CatalogVersion;
            Editor = new ScheduleEditorViewModel(
                $"/jobs/{Uri.EscapeDataString(record.JobId)}/edit",
                ScheduleFormInput.FromDefinition(record.Definition),
                AppFormatting.PrettyJson(record.ScheduleJson),
                record.CatalogVersion,
                true,
                "Save job",
                catalog.HasStarted(record.JobId),
                ScheduleEditorViewModel.BuildOptions(catalog, record.JobId));
            return Page();
        }

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
            var useRawJson = string.Equals(FormMode, "json", StringComparison.OrdinalIgnoreCase)
                || (Request.Form.ContainsKey("scheduleJson") && !Request.Form.ContainsKey("Input.ActivityId"));
            var scheduleJson = useRawJson
                ? ScheduleJson
                : Input.ToScheduleJson();

            try
            {
                jobs.Replace(jobId, scheduleJson, ExpectedVersion, actor: "local-web");
                return Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
            }
            catch (ApplicationProblemException ex) when (ex.Code == "etag-mismatch")
            {
                TempData[CatalogConflictFeedback.TempDataKey] = CatalogConflictFeedback.Message;
                return Redirect($"/jobs/{Uri.EscapeDataString(jobId)}/edit");
            }
            catch (ApplicationProblemException ex)
            {
                Response.StatusCode = ex.StatusCode;
                ErrorMessage = ex.Message;
                ScheduleJson = scheduleJson;
                if (useRawJson)
                {
                    Input = ScheduleFormInput.FromJson(scheduleJson);
                }

                Editor = new ScheduleEditorViewModel($"/jobs/{Uri.EscapeDataString(JobId)}/edit", Input, ScheduleJson, ExpectedVersion, true, "Save job", catalog.HasStarted(JobId), ScheduleEditorViewModel.BuildOptions(catalog, JobId));
                return Page();
            }
        }
    }
}
