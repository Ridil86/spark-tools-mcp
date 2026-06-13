/*
 * Plugin: Spark Tools MCP
 *
 * spark_delete_entry — delete a Spark database entry (and its extension data).
 *
 * By default refuses if any *external* entry references the target. Extension
 * data assets that decorate the target being deleted are NOT considered blocking
 * — Spark's DatabaseTabAssetOperations.DeleteAsset cascades into
 * ExtensionDataCleanupUtility and removes them along with the entry, so they
 * were never going to be dangling. We surface those as `co_deleted_extensions`
 * in the response so the caller knows what got cleaned up.
 *
 * Params:
 *   id    (string, required)
 *   force (bool, optional, default false) — override the external-reference block
 *
 * Returns on success:
 *   {id, deleted, asset_path, entry_type, extension_count, co_deleted_extensions, forced}
 *
 * Returns on refuse:
 *   error + {blocking_references, co_deleted_extensions}
 *   (co_deleted_extensions is included on refuse too, so the caller sees the
 *   full delete impact without running spark_find_references separately.)
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
        "spark_delete_entry",
        Description = "Delete a Spark database entry and its extension data. Refuses by default if external entries reference the target (use spark_find_references first to see what). Extension data decorating the target being deleted is auto-cascaded by Spark's cleanup utility and reported as co_deleted_extensions — it never blocks. Pass force=true to delete anyway when external references exist; those will become dangling (spark_validate_database will report them).",
        Group = "core"
    )]
    public static class DeleteEntryHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("ID of the entry to delete.")]
            public string id { get; set; }

            [ToolParameter("Delete even if external entries reference the target (those references become dangling). Default false.", Required = false)]
            public bool force { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var id = @params.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return McpResult.Error("id is required.");

            var force = @params.Value<bool?>("force") ?? false;

            try
            {
                var entry = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(id);
                if (entry == null)
                    return McpResult.Error($"No SparkDatabaseEntry with id '{id}'. (Already deleted or never existed.)");

                var assetPath = AssetDatabase.GetAssetPath(entry);
                var entryType = entry.GetType();

                // Split references into external (blocking) vs. extension data that decorates
                // this entry (auto-cleaned, non-blocking). ReferenceWalker only adds extension_data
                // hits when ext.GetTargetId() == id, so every extension_data hit here is self-decoration.
                var allRefs = ReferenceWalker.FindReferencesTo(id);
                var externalRefs = allRefs.Where(h => h.ReferenceKind != "extension_data").ToList();
                var coDeletedExtensions = allRefs.Where(h => h.ReferenceKind == "extension_data").ToList();

                if (!force && externalRefs.Count > 0)
                {
                    return McpResult.Error(
                        $"{externalRefs.Count} external reference(s) prevent deletion of '{id}'. Pass force=true to delete anyway (references will become dangling).",
                        new JObject
                        {
                            ["id"] = id,
                            ["asset_path"] = assetPath,
                            ["entry_type"] = entryType.Name,
                            ["blocking_reference_count"] = externalRefs.Count,
                            ["blocking_references"] = new JArray(externalRefs.Select(h => (JToken)h.ToJson()).ToArray()),
                            ["co_deleted_extension_count"] = coDeletedExtensions.Count,
                            ["co_deleted_extensions"] = new JArray(coDeletedExtensions.Select(h => (JToken)h.ToJson()).ToArray()),
                        });
                }

                var ops = new DatabaseTabAssetOperations(entryType);
                bool deleted;
                int extensionCount;
                try
                {
                    deleted = ops.DeleteAsset(entry, out extensionCount);
                }
                catch (System.Exception ex)
                {
                    return McpResult.Error($"DatabaseTabAssetOperations.DeleteAsset threw: {ex.Message}",
                        new JObject { ["asset_path"] = assetPath, ["entry_type"] = entryType.Name });
                }

                if (!deleted)
                {
                    return McpResult.Error("DeleteAsset returned false. Check the Unity console for Spark's specific error.",
                        new JObject { ["asset_path"] = assetPath, ["entry_type"] = entryType.Name });
                }

                // Force a registry refresh so the now-deleted entry is purged from
                // the in-memory caches (the asset postprocessor's deleted-asset path
                // can't reliably identify the type after the asset is gone).
                SparkDatabaseRegistry.Refresh();
                SparkExtensionRegistry.Refresh();

                return McpResult.Success(new JObject
                {
                    ["id"] = id,
                    ["deleted"] = true,
                    ["asset_path"] = assetPath,
                    ["entry_type"] = entryType.Name,
                    ["extension_count"] = extensionCount,
                    ["co_deleted_extensions"] = new JArray(coDeletedExtensions.Select(h => (JToken)h.ToJson()).ToArray()),
                    ["forced"] = force,
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_delete_entry crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
