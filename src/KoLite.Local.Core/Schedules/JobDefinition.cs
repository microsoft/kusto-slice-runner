using System.Text.Json;

namespace KoLite.Local.Core.Schedules
{
    public static class JobDescription
    {
        public const int MaxLength = 65_536;
    }

    public static class JobChunks
    {
        public const int MinCount = 1;
        public const int MaxCount = 32;
    }

    public sealed record JobDefinition
    {
        // Durable, opaque job identity (GUID, "N" format). Null only for freshly authored
        // schedules that have not yet been assigned an id by the catalog. Immutable once set.
        public string? Id { get; init; }

        // Mutable, unique, human-facing label. May be renamed; never the durable identity.
        public required string ActivityId { get; init; }
        public string? Description { get; init; }
        public required string FunctionName { get; init; }
        public required string OutputTable { get; init; }
        public required TimeSpan QueryWindowSize { get; init; }
        public required TimeSpan DelayFromUtcNow { get; init; }
        public required int MaxParallelism { get; init; }
        public required TimeSpan QueryTimeout { get; init; }
        public int? Chunks { get; init; }
        public required DateTimeOffset StartFrom { get; init; }
        public required JobTarget Target { get; init; }
        public bool IsPaused { get; init; }
        public DateTimeOffset? EndOn { get; init; }
        public string? Folder { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<DependentJob> DependsOn { get; init; } = Array.Empty<DependentJob>();
        public JsonElement? JobSettings { get; init; }

        // Determines how the dashboard scores this job's health. Complete (default, strict)
        // additionally flags unaddressed historical gaps (terminal dead-letters) even when
        // recent slices are healthy; Recent ignores old gaps and colors purely by the recent
        // slice trend. Immutable identity/timing rules are unaffected by this field.
        public JobHealthPolicy HealthPolicy { get; init; } = JobHealthPolicy.Complete;
    }

    // Per-job dashboard health policy. Complete is the strict default: the job wants every
    // slice eventually filled, so unaddressed terminal gaps are surfaced. Recent means the
    // operator only cares about the recent trend and old gaps should not raise attention.
    public enum JobHealthPolicy
    {
        Complete = 0,
        Recent = 1
    }

    public sealed record JobTarget
    {
        public required string ClusterUri { get; init; }
        public required string Database { get; init; }
    }

    // Upstream dependency reference. Stored canonical form carries the upstream Id (GUID);
    // user-authored JSON may instead carry ActivityId, which the catalog resolves to an Id.
    public sealed record DependentJob
    {
        public string? Id { get; init; }
        public string? ActivityId { get; init; }
    }
}
