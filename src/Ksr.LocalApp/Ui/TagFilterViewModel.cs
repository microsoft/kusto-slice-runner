// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Schedules;

namespace Ksr.LocalApp.Ui
{
    public sealed record TagFilterViewModel(
        string BasePath,
        string? Range,
        IReadOnlyList<JobTagSummary> Tags,
        IReadOnlyList<string> SelectedTags,
        bool Bare = false,
        DashboardSort? Sort = null)
    {
        public bool HasTags => Tags.Count > 0 || SelectedTags.Count > 0;

        public string SelectedHref => BuildHref(SelectedTags);

        public string ClearHref => BuildHref(Array.Empty<string>());

        public string ToggleHref(string tag)
        {
            var nextTags = SelectedTags.Contains(tag, StringComparer.Ordinal)
                ? SelectedTags.Where(selected => !StringComparer.Ordinal.Equals(selected, tag))
                : SelectedTags.Concat([tag]);

            return BuildHref(nextTags);
        }

        private string BuildHref(IEnumerable<string> tags)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(Range))
            {
                query.Add("range=" + Uri.EscapeDataString(Range));
            }

            query.AddRange(ScheduleTags.NormalizeDistinct(tags).Select(tag => "tag=" + Uri.EscapeDataString(tag)));
            if (Sort is not null && !Sort.IsDefault)
            {
                query.Add("sort=" + Uri.EscapeDataString(Sort.Key));
                query.Add("dir=" + Sort.Direction);
            }

            return query.Count == 0 ? BasePath : BasePath + "?" + string.Join("&", query);
        }
    }
}
