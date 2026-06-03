using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class NewModel : PageModel
    {
        public ScheduleEditorViewModel Editor { get; private set; } = new(
            "/catalog/create",
            ScheduleFormInput.Default(),
            AppFormatting.PrettyJson(SampleScheduleFactory.CreateJson()),
            null,
            false,
            "Create job");

        public void OnGet()
        {
        }
    }
}
