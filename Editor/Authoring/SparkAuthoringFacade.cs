/*
 * Plugin: Spark Tools MCP
 *
 * The single class every spark_* mutation handler calls into. Composes the
 * pieces of the authoring pipeline:
 *
 *   resolve path → generate ID → DatabaseTabAssetOperations.CreateAsset →
 *   apply remaining fields via reflection → SaveAssetIfDirty
 *
 * Spark's `SparkDatabaseAssetPostprocessor` (see SparkDatabaseRegistry.cs)
 * fires on the resulting SaveAssets() call, which auto-refreshes the
 * registry — we do NOT call SparkDatabaseRegistry.Refresh() manually.
 *
 * Returns a CreateResult so handlers can produce structured success/error
 * payloads with the same shape regardless of which entry type was authored.
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class CreateResult
    {
        internal bool Success { get; set; }
        internal string Error { get; set; }
        internal string Id { get; set; }
        internal string AssetPath { get; set; }
        internal string EntryTypeName { get; set; }
        internal string PluginName { get; set; }
        internal List<string> AppliedFields { get; set; }
        internal List<JObject> SkippedFields { get; set; }

        internal JObject ToData()
        {
            return new JObject
            {
                ["id"] = Id,
                ["asset_path"] = AssetPath,
                ["entry_type"] = EntryTypeName,
                ["owning_plugin"] = PluginName,
                ["applied_fields"] = AppliedFields != null ? new JArray(AppliedFields) : new JArray(),
                ["skipped_fields"] = SkippedFields != null ? new JArray(SkippedFields.ConvertAll(o => (JToken)o).ToArray()) : new JArray(),
            };
        }
    }

    internal static class SparkAuthoringFacade
    {
        /// <summary>
        /// Create one new SparkDatabaseEntry of the given type, populating fields.
        ///
        /// The `entryName` field on SparkDatabaseEntry is the filename + internal label.
        /// We require it to be provided (either as a top-level <paramref name="entryName"/>
        /// or as `entryName` in <paramref name="fields"/>) because asset filenames depend
        /// on it. Everything else is optional.
        /// </summary>
        internal static CreateResult Create(
            string entryTypeName,
            JObject fields,
            string explicitId = null,
            string explicitEntryName = null,
            string pathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(entryTypeName))
                return new CreateResult { Success = false, Error = "entry_type is required" };

            // 1. Resolve which plugin owns this entry type and where it should be saved.
            var resolution = PathResolver.Resolve(entryTypeName);
            if (resolution == null)
            {
                return new CreateResult
                {
                    Success = false,
                    Error = $"No PluginManifest declares an entry type named '{entryTypeName}'. Call spark_list_entry_types to see what's available.",
                };
            }

            // 2. Determine the entryName. Required (used as filename).
            string entryName = explicitEntryName;
            if (string.IsNullOrWhiteSpace(entryName) && fields != null && fields["entryName"] != null)
            {
                entryName = fields.Value<string>("entryName");
            }
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return new CreateResult
                {
                    Success = false,
                    Error = "entry_name is required (either as a top-level param or as 'entryName' in fields).",
                    EntryTypeName = entryTypeName,
                    PluginName = resolution.PluginName,
                };
            }

            // 3. Determine the save path. Default from the manifest's TabConfig; allow override
            //    for callers who want a sub-folder (e.g. variations under template's folder).
            var savePath = string.IsNullOrWhiteSpace(pathOverride) ? resolution.SavePath : pathOverride;
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return new CreateResult
                {
                    Success = false,
                    Error = $"Manifest tab for '{entryTypeName}' has no save path configured. Pass `path_override` explicitly.",
                    EntryTypeName = entryTypeName,
                    PluginName = resolution.PluginName,
                };
            }

            // 4. Generate or accept the entry ID.
            var id = string.IsNullOrWhiteSpace(explicitId) ? IdGenerator.New() : explicitId;

            // 5. Create the SO via Spark's own authoring API.
            //    This sets id, entryName, displayName, then AssetDatabase.CreateAsset + SaveAssets + Refresh.
            //    The SparkDatabaseAssetPostprocessor will register it into SparkDatabaseRegistry.
            var ops = new DatabaseTabAssetOperations(resolution.AssetType);
            ScriptableObject asset;
            try
            {
                asset = ops.CreateAsset(id, entryName, savePath);
            }
            catch (Exception ex)
            {
                return new CreateResult
                {
                    Success = false,
                    Error = $"DatabaseTabAssetOperations.CreateAsset threw: {ex.Message}",
                    EntryTypeName = entryTypeName,
                    PluginName = resolution.PluginName,
                };
            }
            if (asset == null)
            {
                return new CreateResult
                {
                    Success = false,
                    Error = $"DatabaseTabAssetOperations.CreateAsset returned null. Check the Unity console for details (Spark logs the specific cause).",
                    EntryTypeName = entryTypeName,
                    PluginName = resolution.PluginName,
                };
            }

            var assetPath = AssetDatabase.GetAssetPath(asset);

            // 6. Apply any additional fields. Skip the three Spark already set (it would just no-op).
            //    Allow callers to overwrite them if they explicitly passed `displayName` etc.
            FieldSetResult applyResult = null;
            if (fields != null && fields.Count > 0)
            {
                applyResult = FieldSetter.Apply(asset, fields);
                if (applyResult.AppliedFields.Count > 0)
                {
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                }
            }

            return new CreateResult
            {
                Success = true,
                Id = id,
                AssetPath = assetPath,
                EntryTypeName = resolution.AssetType.Name,
                PluginName = resolution.PluginName,
                AppliedFields = applyResult?.AppliedFields ?? new List<string>(),
                SkippedFields = applyResult?.SkippedFields ?? new List<JObject>(),
            };
        }
    }
}
