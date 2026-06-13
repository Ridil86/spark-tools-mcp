/*
 * Plugin: Spark Tools MCP
 *
 * spark_batch_create_from_template — clone a template N times with per-variation
 * overrides. Each variation is {entry_name, overrides?}. Reports successes and
 * failures separately so one bad variation doesn't lose the rest of the batch.
 *
 * Params:
 *   template_id (string, required)
 *   variations  (array, required) — list of {entry_name, overrides?}
 *
 * Returns:
 *   {
 *     created_count: N,
 *     failed_count: M,
 *     created: [{id, source_id, asset_path, entry_type, applied_fields, skipped_fields}, ...],
 *     failed:  [{index, entry_name, error}, ...]
 *   }
 *
 * The Spark asset postprocessor batches all the registry updates automatically;
 * after this call returns, all created entries are queryable via SparkDatabaseRegistry.
 */

using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_batch_create_from_template",
        Description = "Clone a Spark database entry N times with per-variation overrides. Each variation has its own entry_name and a field-overrides dict. Continues on partial failures and reports which variations succeeded vs. failed. Extension data (combat stats, quest objectives, etc.) is duplicated for each variation. Use this when you have an explicit list of variations; prefer spark_generate_variations when the variations follow a numeric pattern.",
        Group = "core"
    )]
    public static class BatchCreateHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("ID of the template entry to clone for each variation.")]
            public string template_id { get; set; }

            [ToolParameter("Non-empty array of variation objects; each needs at least entry_name and may include an overrides object of field -> value pairs.")]
            public object[] variations { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var templateId = @params.Value<string>("template_id") ?? @params.Value<string>("templateId");
            if (string.IsNullOrWhiteSpace(templateId))
                return McpResult.Error("template_id is required.");

            var variationsToken = @params["variations"] as JArray;
            if (variationsToken == null || variationsToken.Count == 0)
                return McpResult.Error("variations is required and must be a non-empty array.");

            try
            {
                var created = new List<JObject>();
                var failed = new List<JObject>();
                int index = 0;

                foreach (var v in variationsToken)
                {
                    var variation = v as JObject;
                    if (variation == null)
                    {
                        failed.Add(new JObject
                        {
                            ["index"] = index,
                            ["error"] = "Each variation must be a JSON object with at least an entry_name.",
                        });
                        index++;
                        continue;
                    }

                    var entryName = variation.Value<string>("entry_name") ?? variation.Value<string>("entryName");
                    var overrides = variation["overrides"] as JObject;

                    var result = DuplicationFacade.Duplicate(templateId, entryName, overrides);
                    if (result.Success)
                    {
                        created.Add(result.ToData());
                    }
                    else
                    {
                        failed.Add(new JObject
                        {
                            ["index"] = index,
                            ["entry_name"] = entryName,
                            ["error"] = result.Error,
                        });
                    }
                    index++;
                }

                return McpResult.Success(new JObject
                {
                    ["template_id"] = templateId,
                    ["created_count"] = created.Count,
                    ["failed_count"] = failed.Count,
                    ["created"] = new JArray(created.Cast<JToken>().ToArray()),
                    ["failed"] = new JArray(failed.Cast<JToken>().ToArray()),
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_batch_create_from_template crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
