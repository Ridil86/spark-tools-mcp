/*
 * Plugin: Spark Tools MCP
 *
 * Wraps DatabaseTabAssetOperations.DuplicateAssetWithData and layers overrides
 * on top via FieldSetter. Single shared entry point for the three batch-side
 * tools (duplicate_entry, batch_create_from_template, generate_variations) so
 * we only have one place to maintain the post-duplicate save and extension-data
 * preservation logic.
 *
 * Spark's DuplicateAssetWithData already:
 *   - copies the source asset (including extension data via DuplicateExtensionDataForEntry)
 *   - assigns a new id and entryName
 *   - sets displayName to entryName when displayName arg is empty
 *   - calls EditorUtility.SetDirty + AssetDatabase.SaveAssets + Refresh
 *
 * On top of that we:
 *   - generate a fresh ID in Spark's canonical format
 *   - apply caller-supplied field overrides via FieldSetter
 *   - re-save if anything changed
 *
 * Returns a DuplicateResult mirror of CreateResult so handlers can produce
 * uniform success/error payloads.
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class DuplicateResult
    {
        internal bool Success { get; set; }
        internal string Error { get; set; }
        internal string Id { get; set; }
        internal string AssetPath { get; set; }
        internal string EntryTypeName { get; set; }
        internal string SourceId { get; set; }
        internal List<string> AppliedFields { get; set; }
        internal List<JObject> SkippedFields { get; set; }

        internal JObject ToData()
        {
            return new JObject
            {
                ["id"] = Id,
                ["source_id"] = SourceId,
                ["asset_path"] = AssetPath,
                ["entry_type"] = EntryTypeName,
                ["applied_fields"] = AppliedFields != null ? new JArray(AppliedFields) : new JArray(),
                ["skipped_fields"] = SkippedFields != null ? new JArray(SkippedFields.ConvertAll(o => (JToken)o).ToArray()) : new JArray(),
            };
        }
    }

    internal static class DuplicationFacade
    {
        /// <summary>
        /// Clone an existing entry, optionally overriding fields on the clone.
        ///
        /// <paramref name="newEntryName"/> is required because the asset filename derives
        /// from it. <paramref name="overrides"/> may include displayName/description/icon
        /// (those overwrite whatever DuplicateAssetWithData set) plus any other fields.
        /// </summary>
        internal static DuplicateResult Duplicate(string sourceId, string newEntryName, JObject overrides)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                return new DuplicateResult { Success = false, Error = "source_id is required.", SourceId = sourceId };

            if (string.IsNullOrWhiteSpace(newEntryName))
                return new DuplicateResult { Success = false, Error = "new_entry_name is required.", SourceId = sourceId };

            var source = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(sourceId);
            if (source == null)
            {
                return new DuplicateResult
                {
                    Success = false,
                    Error = $"No SparkDatabaseEntry with id '{sourceId}'. Run spark_list_entries (Phase 4) or check the Spark Editor.",
                    SourceId = sourceId,
                };
            }

            var sourceType = source.GetType();
            var ops = new DatabaseTabAssetOperations(sourceType);
            var newId = IdGenerator.New();

            // Pull displayName/description/icon out of overrides up front so they go
            // through DuplicateAssetWithData rather than getting overwritten by it.
            string overrideDisplayName = overrides?.Value<string>("displayName");
            string overrideDescription = overrides?.Value<string>("description");
            Sprite overrideIcon = null;
            if (overrides?["icon"] != null)
            {
                var iconRef = overrides.Value<string>("icon");
                if (!string.IsNullOrEmpty(iconRef))
                {
                    overrideIcon = LoadSprite(iconRef);
                }
            }

            ScriptableObject duplicated;
            try
            {
                duplicated = ops.DuplicateAssetWithData(source, newId, newEntryName, overrideDisplayName, overrideDescription, overrideIcon);
            }
            catch (Exception ex)
            {
                return new DuplicateResult
                {
                    Success = false,
                    Error = $"DuplicateAssetWithData threw: {ex.Message}",
                    SourceId = sourceId,
                    EntryTypeName = sourceType.Name,
                };
            }

            if (duplicated == null)
            {
                return new DuplicateResult
                {
                    Success = false,
                    Error = "DuplicateAssetWithData returned null. Check Unity console for Spark's specific error.",
                    SourceId = sourceId,
                    EntryTypeName = sourceType.Name,
                };
            }

            var assetPath = AssetDatabase.GetAssetPath(duplicated);

            // Apply any remaining overrides (anything other than the three we already handled).
            FieldSetResult applyResult = null;
            if (overrides != null && overrides.Count > 0)
            {
                var residual = StripHandledKeys(overrides);
                if (residual.Count > 0)
                {
                    applyResult = FieldSetter.Apply(duplicated, residual);
                    if (applyResult.AppliedFields.Count > 0)
                    {
                        EditorUtility.SetDirty(duplicated);
                        AssetDatabase.SaveAssetIfDirty(duplicated);
                    }
                }
            }

            return new DuplicateResult
            {
                Success = true,
                Id = newId,
                AssetPath = assetPath,
                EntryTypeName = sourceType.Name,
                SourceId = sourceId,
                AppliedFields = applyResult?.AppliedFields ?? new List<string>(),
                SkippedFields = applyResult?.SkippedFields ?? new List<JObject>(),
            };
        }

        private static JObject StripHandledKeys(JObject overrides)
        {
            var residual = new JObject();
            foreach (var prop in overrides.Properties())
            {
                if (prop.Name == "displayName" || prop.Name == "description" || prop.Name == "icon")
                    continue;
                residual[prop.Name] = prop.Value;
            }
            return residual;
        }

        private static Sprite LoadSprite(string reference)
        {
            if (reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<Sprite>(reference);
            }
            var guidPath = AssetDatabase.GUIDToAssetPath(reference);
            if (!string.IsNullOrEmpty(guidPath)) return AssetDatabase.LoadAssetAtPath<Sprite>(guidPath);
            return null;
        }
    }
}
