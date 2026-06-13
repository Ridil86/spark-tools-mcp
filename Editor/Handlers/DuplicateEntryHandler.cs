/*
 * Plugin: Spark Tools MCP
 *
 * spark_duplicate_entry — clone one entry, optionally overriding fields on the
 * clone. The building block for batch_create_from_template and generate_variations.
 *
 * Params:
 *   source_id      (string, required)
 *   new_entry_name (string, required)
 *   overrides      (object, optional) — field → value pairs applied after the clone
 *
 * Returns: {id, source_id, asset_path, entry_type, applied_fields, skipped_fields}
 */

using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_duplicate_entry",
        Description = "Clone a Spark database entry, optionally overriding fields on the clone. Generates a fresh ID in Spark's canonical format. Uses Spark's own DuplicateAssetWithData so extension data (combat stats, quest objectives, etc.) is copied alongside. Returns {id, source_id, asset_path, entry_type, applied_fields, skipped_fields}. For bulk operations prefer spark_batch_create_from_template or spark_generate_variations.",
        Group = "core"
    )]
    public static class DuplicateEntryHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("ID of the entry to clone.")]
            public string source_id { get; set; }

            [ToolParameter("Internal label / asset filename source for the clone.")]
            public string new_entry_name { get; set; }

            [ToolParameter("Optional object of field -> value pairs applied to the clone after duplication.", Required = false)]
            public object overrides { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var sourceId = @params.Value<string>("source_id") ?? @params.Value<string>("sourceId");
            var newEntryName = @params.Value<string>("new_entry_name") ?? @params.Value<string>("newEntryName");
            var overrides = @params["overrides"] as JObject;

            try
            {
                var result = DuplicationFacade.Duplicate(sourceId, newEntryName, overrides);
                if (!result.Success)
                {
                    return McpResult.Error(result.Error, new JObject
                    {
                        ["source_id"] = result.SourceId,
                        ["entry_type"] = result.EntryTypeName,
                    });
                }
                return McpResult.Success(result.ToData());
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_duplicate_entry crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
