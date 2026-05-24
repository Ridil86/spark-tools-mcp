/*
 * Plugin: Spark Tools MCP
 *
 * spark_ping — smoke-test tool. Returns a small payload that proves three things:
 *   1. Unity MCP's CommandRegistry discovered this handler (the tool is callable at all).
 *   2. Spark's SparkDatabaseRegistry is reachable (entry_count is non-zero in a Spark project).
 *   3. Spark's PluginManifest assets are discoverable (we list them so future handlers
 *      can use PluginManifest.GetAllTabs() for path resolution).
 *
 * This handler is intentionally minimal — it exists to validate the architecture before
 * Phase 1 starts building actual authoring tools.
 */

using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_ping",
        Description = "Smoke-test the Spark Tools MCP installation. Returns counts of Spark database entries and plugin manifests, plus the spark-tools-mcp package version. Use this to confirm the toolchain is wired up correctly before invoking other spark_* tools.",
        Group = "core"
    )]
    public static class PingHandler
    {
        private const string PackageVersion = "0.1.0";

        public static object HandleCommand(JObject @params)
        {
            try
            {
                int entryCount = SparkDatabaseRegistry.GetTotalEntryCount();

                var manifestGuids = AssetDatabase.FindAssets("t:PluginManifest");
                var manifests = manifestGuids
                    .Select(g => AssetDatabase.LoadAssetAtPath<PluginManifest>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(m => m != null)
                    .Select(m => new JObject
                    {
                        ["plugin_name"] = m.pluginName,
                        ["unique_id"] = m.uniqueID,
                        ["version"] = m.version,
                        ["category_count"] = m.Categories?.Count ?? 0,
                        ["tab_count"] = m.Categories?.Sum(c => c.Tabs?.Count ?? 0) ?? 0,
                    })
                    .ToList();

                var payload = new JObject
                {
                    ["spark_tools_mcp_version"] = PackageVersion,
                    ["spark_entry_count"] = entryCount,
                    ["plugin_manifest_count"] = manifests.Count,
                    ["plugin_manifests"] = new JArray(manifests.Cast<JToken>().ToArray()),
                    ["unity_version"] = Application.unityVersion,
                };

                return McpResult.Success(payload);
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_ping failed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
