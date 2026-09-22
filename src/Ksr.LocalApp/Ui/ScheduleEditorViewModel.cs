// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Catalog;

namespace Ksr.LocalApp.Ui
{
    // A selectable upstream job for the dependency picker. Value posted is the durable GUID Id;
    // ActivityId is the human-facing label shown in the dropdown and chips.
    public sealed record DependencyOption(string Id, string ActivityId);

    // A currently-selected dependency rendered as a chip. Value is the stored token (GUID, or a
    // legacy activityId). Label is the resolved upstream name. Resolved is false for a token that
    // does not match any current job (e.g. a deleted upstream), so the UI can flag it.
    public sealed record DependencyChip(string Value, string Label, bool Resolved);

    public sealed record ScheduleEditorViewModel(
        string PostAction,
        ScheduleFormInput Input,
        string ScheduleJson,
        long? ExpectedVersion,
        bool ExistingJob,
        string SubmitLabel,
        bool HasStarted = false,
        IReadOnlyList<DependencyOption>? AvailableDependencies = null)
    {
        public bool StartedFieldsReadOnly => ExistingJob && HasStarted;

        public IReadOnlyList<DependencyOption> DependencyOptions => AvailableDependencies ?? Array.Empty<DependencyOption>();

        // Resolves the current Input.DependsOn tokens (one per GUID/activityId) into display chips,
        // mapping GUID ids and legacy activityId tokens to the upstream job's current name.
        public IReadOnlyList<DependencyChip> CurrentDependencies
        {
            get
            {
                var byId = new Dictionary<string, string>(StringComparer.Ordinal);
                var activityIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in DependencyOptions)
                {
                    byId[option.Id] = option.ActivityId;
                    activityIds.Add(option.ActivityId);
                }

                var chips = new List<DependencyChip>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in SplitTokens(Input.DependsOn))
                {
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    if (byId.TryGetValue(token, out var name))
                    {
                        chips.Add(new DependencyChip(token, name, Resolved: true));
                    }
                    else if (activityIds.Contains(token))
                    {
                        chips.Add(new DependencyChip(token, token, Resolved: true));
                    }
                    else
                    {
                        chips.Add(new DependencyChip(token, token, Resolved: false));
                    }
                }

                return chips;
            }
        }

        public static IReadOnlyList<DependencyOption> BuildOptions(SqliteJobCatalogRepository catalog, string? excludeJobId) =>
            catalog.List()
                .Where(record => excludeJobId is null || !StringComparer.Ordinal.Equals(record.JobId, excludeJobId))
                .OrderBy(record => record.ActivityId, StringComparer.Ordinal)
                .Select(record => new DependencyOption(record.JobId, record.ActivityId))
                .ToList();

        private static IEnumerable<string> SplitTokens(string? value) =>
            (value ?? string.Empty).Split(['\r', '\n', ',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
