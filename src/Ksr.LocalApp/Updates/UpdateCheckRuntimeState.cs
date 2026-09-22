// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.LocalApp.Updates
{
    public sealed record UpdateCheckSnapshot(
        UpdateCheckStatus Status,
        UpdateCheckUnavailableReason Reason,
        string? BuiltSha,
        string? RemoteSha,
        int? CommitsBehind,
        int? CommitsAhead,
        DateTimeOffset? LastCheckedUtc,
        string? ErrorMessage,
        string? LatestVersion = null,
        string? ReleaseUrl = null)
    {
        public static UpdateCheckSnapshot Initial(bool enabled, string? builtSha)
        {
            if (!enabled)
            {
                return new UpdateCheckSnapshot(
                    UpdateCheckStatus.Unavailable,
                    UpdateCheckUnavailableReason.Disabled,
                    builtSha,
                    RemoteSha: null,
                    CommitsBehind: null,
                    CommitsAhead: null,
                    LastCheckedUtc: null,
                    ErrorMessage: null);
            }

            if (string.IsNullOrWhiteSpace(builtSha))
            {
                return new UpdateCheckSnapshot(
                    UpdateCheckStatus.Unavailable,
                    UpdateCheckUnavailableReason.NoBuildSha,
                    BuiltSha: null,
                    RemoteSha: null,
                    CommitsBehind: null,
                    CommitsAhead: null,
                    LastCheckedUtc: null,
                    ErrorMessage: null);
            }

            return new UpdateCheckSnapshot(
                UpdateCheckStatus.Checking,
                UpdateCheckUnavailableReason.None,
                builtSha,
                RemoteSha: null,
                CommitsBehind: null,
                CommitsAhead: null,
                LastCheckedUtc: null,
                ErrorMessage: null);
        }
    }

    public sealed class UpdateCheckRuntimeState
    {
        private readonly object gate = new();
        private UpdateCheckSnapshot snapshot;

        public UpdateCheckRuntimeState(UpdateCheckSnapshot initial)
        {
            snapshot = initial;
        }

        public UpdateCheckSnapshot GetSnapshot()
        {
            lock (gate)
            {
                return snapshot;
            }
        }

        public void Update(UpdateCheckSnapshot next)
        {
            lock (gate)
            {
                snapshot = next;
            }
        }
    }
}
