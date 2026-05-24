/*
 * Plugin: Spark Tools MCP
 *
 * spark_list_entry_types — enumerate every SparkDatabaseEntry subclass declared
 * by any PluginManifest in the project. One row per type: name, full_name,
 * owning_plugin, asmdef, default_save_path, entry_count (how many SOs of this
 * type currently exist).
 *
 * This is the starting point for any model-driven authoring workflow: call it
 * first to discover what's available, then spark_schema for the one you care
 * about, then spark_create_entry.
 */

using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_list_entry_types",
        Description = "List every Spark database entry type (subclass of SparkDatabaseEntry) declared by any PluginManifest in the project. Each row includes the type name, full name, owning plugin, asmdef, the canonical save path, and the current instance count in the registry. Use this before spark_schema or spark_create_entry to discover what's available.",
        Group = "core"
    )]
    public static class ListEntryTypesHandler
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                var rows = new JArray();
                int total = 0;

                foreach (var resolution in PathResolver.AllResolutions())
                {
                    // SparkDatabaseRegistry indexes entriesByType by the actual SO type, not its
                    // base class — so calling GetAllEntries<SparkDatabaseEntry>() returns nothing.
                    // Invoke the generic method reflectively with the concrete type instead.
                    int entryCount = CountEntriesOfType(resolution.AssetType);

                    rows.Add(new JObject
                    {
                        ["entry_type"] = resolution.AssetType.Name,
                        ["full_name"] = resolution.AssetType.FullName,
                        ["owning_plugin"] = resolution.PluginName,
                        ["asmdef"] = resolution.Tab.assetAsmdef,
                        ["default_save_path"] = resolution.Tab.path,
                        ["tab_id"] = resolution.Tab.tabId,
                        ["tab_display_name"] = resolution.Tab.displayName,
                        ["entry_count"] = entryCount,
                    });
                    total++;
                }

                return McpResult.Success(new JObject
                {
                    ["entry_type_count"] = total,
                    ["entry_types"] = rows,
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_list_entry_types crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static int CountEntriesOfType(System.Type entryType)
        {
            // Manifests may declare SOs that aren't SparkDatabaseEntry subclasses
            // (e.g. *PluginSettings types). The generic method requires that constraint,
            // so for non-DB types we fall back to a plain AssetDatabase search.
            if (!typeof(SparkDatabaseEntry).IsAssignableFrom(entryType))
            {
                var guids = UnityEditor.AssetDatabase.FindAssets($"t:{entryType.Name}");
                return guids?.Length ?? 0;
            }

            // SparkDatabaseRegistry.GetAllEntries<T> is generic on the SO's actual type.
            // We resolve it reflectively so this handler doesn't have to hardcode every type.
            var method = typeof(SparkDatabaseRegistry).GetMethod(
                nameof(SparkDatabaseRegistry.GetAllEntries),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null) return 0;

            var generic = method.MakeGenericMethod(entryType);
            var list = generic.Invoke(null, null) as System.Collections.ICollection;
            return list?.Count ?? 0;
        }
    }
}
