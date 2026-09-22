// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace KoLite.Local.Sqlite.Catalog
{
    public sealed class CatalogVersionConflictException : InvalidOperationException
    {
        public CatalogVersionConflictException(string jobId, long expectedVersion, long? actualVersion)
            : base(actualVersion is { } currentVersion
                ? $"Catalog version conflict for '{jobId}'. Expected {expectedVersion}, found {currentVersion}."
                : $"Catalog version conflict for '{jobId}'. Expected {expectedVersion}, but the job changed before the update completed.")
        {
            JobId = jobId;
            ExpectedVersion = expectedVersion;
            ActualVersion = actualVersion;
        }

        public string JobId { get; }

        public long ExpectedVersion { get; }

        public long? ActualVersion { get; }
    }
}
