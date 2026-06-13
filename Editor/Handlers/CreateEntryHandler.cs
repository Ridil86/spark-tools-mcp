/*
 * Plugin: Spark Tools MCP
 *
 * spark_create_entry — create a single SparkDatabaseEntry of any registered type.
 *
 * Params (all in the JObject @params):
 *   entry_type  (string, required)  — class name of the entry type (e.g. "ItemEntry").
 *   entry_name  (string, required)  — internal label / asset filename source.
 *   fields      (object, optional)  — JSON object of field → value pairs to apply
 *                                     beyond the default id/entryName/displayName.
 *   id          (string, optional)  — explicit ID; auto-generated if omitted.
 *   path        (string, optional)  — override save path; defaults to the manifest's tab path.
 *
 * Returns the structured CreateResult: id, asset_path, entry_type, owning_plugin,
 * applied_fields, skipped_fields. Skipped fields are not fatal — they're reported
 * so the caller can correct inputs and retry.
 */

using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_create_entry",
        Description = "Create one Spark database entry of the given type. Resolves the canonical save folder via PluginManifest, generates a Spark-compatible ID, instantiates the SO, applies any provided field values, and saves. Returns {id, asset_path, entry_type, owning_plugin, applied_fields, skipped_fields}. Spark's asset postprocessor auto-registers the new entry in SparkDatabaseRegistry — no manual refresh needed. To discover available entry types, call spark_list_entry_types (coming in Phase 2).",
        Group = "core"
    )]
    public static class CreateEntryHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("Class name of the entry type to create, e.g. 'ItemEntry' (see spark_list_entry_types).")]
            public string entry_type { get; set; }

            [ToolParameter("Internal label / asset filename source for the new entry.")]
            public string entry_name { get; set; }

            [ToolParameter("Optional object of field -> value pairs to apply beyond the default id/entryName/displayName.", Required = false)]
            public object fields { get; set; }

            [ToolParameter("Optional explicit ID; auto-generated if omitted.", Required = false)]
            public string id { get; set; }

            [ToolParameter("Optional save-path override; defaults to the PluginManifest tab path.", Required = false)]
            public string path { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            // batch_execute normalizes incoming keys to camelCase, while direct invocation
            // preserves whatever the caller sent. Accept both shapes so docs can show
            // snake_case (the MCP convention) while still working via batch_execute.
            var entryType = @params.Value<string>("entry_type") ?? @params.Value<string>("entryType");
            var entryName = @params.Value<string>("entry_name") ?? @params.Value<string>("entryName");
            var explicitId = @params.Value<string>("id");
            var pathOverride = @params.Value<string>("path") ?? @params.Value<string>("path_override") ?? @params.Value<string>("pathOverride");
            var fields = @params["fields"] as JObject;

            try
            {
                var result = SparkAuthoringFacade.Create(
                    entryType,
                    fields,
                    explicitId: explicitId,
                    explicitEntryName: entryName,
                    pathOverride: pathOverride);

                if (!result.Success)
                {
                    return McpResult.Error(result.Error, new JObject
                    {
                        ["entry_type"] = result.EntryTypeName,
                        ["owning_plugin"] = result.PluginName,
                    });
                }

                return McpResult.Success(result.ToData());
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_create_entry crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
