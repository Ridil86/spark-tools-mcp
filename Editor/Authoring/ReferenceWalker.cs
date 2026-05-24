/*
 * Plugin: Spark Tools MCP
 *
 * Find every SparkDatabaseEntry (and SparkDatabaseExtensionData) in the project
 * that references a target entry ID.
 *
 * Uses SerializedObject/SerializedProperty for the walk because Unity's
 * serialization model already handles nested data, lists, SerializeReference,
 * and array elements uniformly — far less error-prone than walking via
 * reflection ourselves. The propertyPath we report comes straight from Unity,
 * so callers can drill into the asset with the same path string.
 *
 * Two reference sources are checked:
 *   1. Object references whose target is a SparkDatabaseEntry with the given id.
 *   2. Extension data assets where GetTargetId() == targetId.
 */

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class ReferenceHit
    {
        internal string ReferencingEntryId;
        internal string ReferencingEntryType;
        internal string ReferencingAssetPath;
        internal string PropertyPath;       // Unity's SerializedProperty path; "" for extension data
        internal string ReferenceKind;      // "database_entry" or "extension_data"

        internal JObject ToJson() => new()
        {
            ["referencing_entry_id"] = ReferencingEntryId,
            ["referencing_entry_type"] = ReferencingEntryType,
            ["referencing_asset_path"] = ReferencingAssetPath,
            ["property_path"] = PropertyPath,
            ["reference_kind"] = ReferenceKind,
        };
    }

    internal static class ReferenceWalker
    {
        /// <summary>
        /// Find every entry and extension-data asset in the project that references
        /// the given target id. Excludes self-references (an entry pointing to itself
        /// via a misconfigured field is reported by spark_validate_database, not here).
        /// </summary>
        internal static List<ReferenceHit> FindReferencesTo(string targetId)
        {
            var hits = new List<ReferenceHit>();
            if (string.IsNullOrEmpty(targetId)) return hits;

            // 1. Walk every SparkDatabaseEntry asset.
            var entryGuids = AssetDatabase.FindAssets("t:SparkDatabaseEntry");
            foreach (var guid in entryGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = AssetDatabase.LoadAssetAtPath<SparkDatabaseEntry>(path);
                if (entry == null) continue;
                if (entry.id == targetId) continue;   // skip self

                using var so = new SerializedObject(entry);
                var prop = so.GetIterator();
                bool enterChildren = true;
                while (prop.Next(enterChildren))
                {
                    enterChildren = !ShouldSkipChildren(prop);
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (prop.objectReferenceValue is SparkDatabaseEntry referenced
                        && !ReferenceEquals(referenced, entry)
                        && referenced.id == targetId)
                    {
                        hits.Add(new ReferenceHit
                        {
                            ReferencingEntryId = entry.id,
                            ReferencingEntryType = entry.GetType().Name,
                            ReferencingAssetPath = path,
                            PropertyPath = prop.propertyPath,
                            ReferenceKind = "database_entry",
                        });
                    }
                }
            }

            // 2. Walk extension data assets — these reference entries by string ID via GetTargetId().
            var extGuids = AssetDatabase.FindAssets("t:SparkDatabaseExtensionData");
            foreach (var guid in extGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ext = AssetDatabase.LoadAssetAtPath<SparkDatabaseExtensionData>(path);
                if (ext == null) continue;
                string extTargetId = null;
                try { extTargetId = ext.GetTargetId(); } catch { /* malformed extension — ignore */ }
                if (string.IsNullOrEmpty(extTargetId) || extTargetId != targetId) continue;

                hits.Add(new ReferenceHit
                {
                    ReferencingEntryId = ext.name,
                    ReferencingEntryType = ext.GetType().Name,
                    ReferencingAssetPath = path,
                    PropertyPath = "",
                    ReferenceKind = "extension_data",
                });
            }

            return hits;
        }

        // SerializedProperty.Next(enterChildren=true) on certain property types
        // (Vector3, Color, AnimationCurve, etc.) descends into internals we don't
        // care about and slows the walk. Skip children on known leaf-style props.
        private static bool ShouldSkipChildren(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Rect:
                case SerializedPropertyType.Bounds:
                case SerializedPropertyType.AnimationCurve:
                case SerializedPropertyType.Gradient:
                case SerializedPropertyType.LayerMask:
                    return true;
                default:
                    return false;
            }
        }
    }
}
