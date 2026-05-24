/*
 * Plugin: Spark Tools MCP
 *
 * spark_find_references — find every SparkDatabaseEntry or extension data asset
 * in the project that references the given target id. Critical before rename
 * or delete: a non-empty result means changing the target will break references.
 *
 * Params:
 *   id (string, required) — the entry id to search for
 *
 * Returns: {target_id, reference_count, references: [{referencing_entry_id,
 * referencing_entry_type, referencing_asset_path, property_path, reference_kind}, ...]}
 */

using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_find_references",
        Description = "Find every entry and extension-data asset in the project that references the given Spark entry id. Walks SerializedObject for accurate property paths, plus checks SparkDatabaseExtensionData.GetTargetId(). Use this before spark_delete_entry (Phase 5) or before renaming an id manually — a non-empty result means the change will break references.",
        Group = "core"
    )]
    public static class FindReferencesHandler
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");
            var id = @params.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                return McpResult.Error("id is required.");

            try
            {
                var hits = ReferenceWalker.FindReferencesTo(id);
                return McpResult.Success(new JObject
                {
                    ["target_id"] = id,
                    ["reference_count"] = hits.Count,
                    ["references"] = new JArray(hits.Select(h => (JToken)h.ToJson()).ToArray()),
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_find_references crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
