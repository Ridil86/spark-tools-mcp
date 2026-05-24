/*
 * Plugin: Spark Tools MCP
 *
 * spark_list_extension_types — enumerate every SparkDatabaseExtensionData
 * subclass loaded in the project. Counterpart to spark_list_entry_types.
 *
 * Each row: {extension_type, full_name, asmdef, asset_count, manifest_path,
 * has_manifest, save_path, extension_name, version}.
 *
 * Types without a PluginExtensionManifest are still listed (with has_manifest:
 * false) — those types are technically present in code but can't be auto-saved
 * to a default path; the caller must provide an explicit path to spark_create_extension.
 *
 * No params.
 */

using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_list_extension_types",
        Description = "List every Spark extension data type (SparkDatabaseExtensionData subclass) discovered in loaded assemblies. Each row reports the type name, asmdef, current asset count, and — when a PluginExtensionManifest exists for the type — the canonical save_path, extension_name, and version. Types without a manifest appear with has_manifest: false; spark_create_extension on those requires an explicit path parameter.",
        Group = "core"
    )]
    public static class ListExtensionTypesHandler
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                var rows = new JArray();
                int withManifest = 0, withoutManifest = 0;

                foreach (var resolution in ExtensionPathResolver.AllResolutions())
                {
                    var manifestPath = resolution.Manifest != null
                        ? AssetDatabase.GetAssetPath(resolution.Manifest)
                        : null;

                    rows.Add(new JObject
                    {
                        ["extension_type"] = resolution.TypeName,
                        ["full_name"] = resolution.ExtensionType.FullName,
                        ["asmdef"] = resolution.Asmdef,
                        ["asset_count"] = resolution.AssetCount,
                        ["has_manifest"] = resolution.HasManifest,
                        ["manifest_path"] = manifestPath,
                        ["save_path"] = resolution.SavePath,
                        ["extension_name"] = resolution.ExtensionName,
                        ["version"] = resolution.Version,
                    });
                    if (resolution.HasManifest) withManifest++;
                    else withoutManifest++;
                }

                return McpResult.Success(new JObject
                {
                    ["extension_type_count"] = rows.Count,
                    ["with_manifest_count"] = withManifest,
                    ["without_manifest_count"] = withoutManifest,
                    ["extension_types"] = rows,
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_list_extension_types crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
