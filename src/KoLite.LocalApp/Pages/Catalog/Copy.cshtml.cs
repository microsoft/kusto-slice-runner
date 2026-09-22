// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class CopyModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public CopyModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        public ScheduleEditorViewModel? Editor { get; private set; }

        public IActionResult OnGet(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            var json = CopySchedule(record.ScheduleJson);
            Editor = new ScheduleEditorViewModel("/jobs/new", ScheduleFormInput.FromJson(json), AppFormatting.PrettyJson(json), null, false, "Create copied job", false, ScheduleEditorViewModel.BuildOptions(catalog, null));
            return Page();
        }

        private static string CopySchedule(string scheduleJson)
        {
            var node = JsonNode.Parse(scheduleJson)!.AsObject();
            node.Remove("id");
            var activityId = node["activityId"]?.GetValue<string>() ?? "copied.job";
            node["activityId"] = $"{activityId}.copy";
            if (node["outputTable"] is not null)
            {
                node["outputTable"] = $"{node["outputTable"]!.GetValue<string>()}_Copy";
            }

            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
        }
    }
}
