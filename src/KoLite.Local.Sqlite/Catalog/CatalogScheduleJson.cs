// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using KoLite.Local.Sqlite.Infrastructure;

namespace KoLite.Local.Sqlite.Catalog
{
    // A resolved upstream dependency edge for storage. UpstreamId (GUID) is the canonical form;
    // ActivityId is preserved only for dangling/unresolved historical edges (migration).
    internal sealed record StoredDependency(string? UpstreamId, string? ActivityId);

    // Transforms schedule JSON between the user/canonical shape and the stored canonical shape,
    // where the durable identity is the top-level GUID `id` and dependsOn edges are id-based.
    internal static class CatalogScheduleJson
    {
        // Produces the stored canonical JSON: injects the durable `id` and rewrites `dependsOn`
        // to id-based edges. Unresolved edges are written as activityId-only (callers that require
        // resolution must validate before calling).
        public static string WriteStorageJson(string canonicalJson, string id, IReadOnlyList<StoredDependency> deps)
        {
            var root = JsonNode.Parse(canonicalJson)?.AsObject()
                ?? throw new InvalidOperationException("Schedule JSON root must be a JSON object.");

            root["id"] = id;
            root.Remove("dependsOn");
            if (deps.Count > 0)
            {
                var array = new JsonArray();
                foreach (var dep in deps)
                {
                    array.Add(dep.UpstreamId is not null
                        ? new JsonObject { ["id"] = dep.UpstreamId }
                        : new JsonObject { ["activityId"] = dep.ActivityId });
                }

                root["dependsOn"] = array;
            }

            return root.ToJsonString(SqliteStorage.JsonOptions);
        }

        // Produces an export-friendly JSON: keeps the id-based edges authoritative but adds the
        // upstream job's current activityId label next to each edge for human readability.
        // Exports are pretty-printed (indented) for readable diffs; see BuildExportNode for the
        // composable node used by multi-job array exports.
        public static string WriteExportJson(string storedJson, IReadOnlyDictionary<string, string> idToActivityId) =>
            BuildExportNode(storedJson, idToActivityId).ToJsonString(SqliteStorage.IndentedJsonOptions);

        // Builds the export-friendly node (id-based dependsOn edges annotated with the upstream
        // job's current activityId). Returned unserialized so multi-job exports can compose a
        // single JsonArray and indent the whole document once.
        public static JsonObject BuildExportNode(string storedJson, IReadOnlyDictionary<string, string> idToActivityId)
        {
            var root = JsonNode.Parse(storedJson)?.AsObject()
                ?? throw new InvalidOperationException("Schedule JSON root must be a JSON object.");

            if (root["dependsOn"] is JsonArray deps)
            {
                var rebuilt = new JsonArray();
                foreach (var dep in deps)
                {
                    var obj = dep?.AsObject();
                    if (obj is null)
                    {
                        continue;
                    }

                    var upstreamId = obj["id"]?.GetValue<string>();
                    var entry = new JsonObject();
                    if (upstreamId is not null)
                    {
                        entry["id"] = upstreamId;
                        if (idToActivityId.TryGetValue(upstreamId, out var activityId))
                        {
                            entry["activityId"] = activityId;
                        }
                    }
                    else if (obj["activityId"]?.GetValue<string>() is { } activityId)
                    {
                        entry["activityId"] = activityId;
                    }

                    rebuilt.Add(entry);
                }

                root["dependsOn"] = rebuilt;
            }

            return root;
        }
    }
}
