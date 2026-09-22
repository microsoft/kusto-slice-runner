// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.Local.Core.Schedules
{
    public sealed class ScheduleValidationResult
    {
        private ScheduleValidationResult(JobDefinition? definition, IReadOnlyList<ScheduleValidationError> errors)
        {
            Definition = definition;
            Errors = errors;
        }

        public JobDefinition? Definition { get; }
        public IReadOnlyList<ScheduleValidationError> Errors { get; }
        public bool IsValid => Definition is not null && Errors.Count == 0;

        public static ScheduleValidationResult Success(JobDefinition definition) => new(definition, Array.Empty<ScheduleValidationError>());
        public static ScheduleValidationResult Failed(params ScheduleValidationError[] errors) => new(null, errors);
        public static ScheduleValidationResult Failed(IEnumerable<ScheduleValidationError> errors) => new(null, errors.ToArray());
    }

    public sealed record ScheduleValidationError(string? ActivityId, string Field, string Message);
}
