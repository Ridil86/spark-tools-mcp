/*
 * Plugin: Spark Tools MCP
 *
 * spark_validate_database — whole-database integrity scan. Run after batch
 * operations or before a build to catch breakage early. Returns a structured
 * report of duplicate ids, dangling references, entries outside Resources/,
 * and entries missing required fields (id or entryName).
 *
 * No params. Safe to run any time — no mutations.
 */

using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Common;
using RockRabbit.SparkToolsMCP.Validation;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_validate_database",
        Description = "Run a whole-database integrity scan and return all findings: duplicate ids, dangling SparkDatabaseEntry references, entries outside any Resources/ folder (won't ship in builds), and entries with missing required fields (empty id or entryName). Run after spark_batch_create_from_template / spark_generate_variations / spark_delete_entry to catch breakage; run before a build to confirm the database is shippable.",
        Group = "core"
    )]
    public static class ValidateDatabaseHandler
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                var report = DatabaseValidator.Run();
                return McpResult.Success(report.ToJson());
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_validate_database crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
