/*
 * Plugin: Spark Tools MCP
 *
 * Whole-database integrity check. Run after batch operations to catch what
 * slipped through. Reports four kinds of problems:
 *
 *   duplicate_ids            — two or more entries share the same id
 *   dangling_references      — a SparkDatabaseEntry field points to a non-existent id
 *   entries_outside_resources — entries that won't be included in builds
 *   missing_required_fields  — empty id or entryName
 *
 * Each finding includes enough context (asset path, entry id, field path) for
 * a caller to act on it.
 */

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Validation
{
    internal sealed class ValidationReport
    {
        internal List<JObject> DuplicateIds { get; } = new();
        internal List<JObject> DanglingReferences { get; } = new();
        internal List<JObject> EntriesOutsideResources { get; } = new();
        internal List<JObject> MissingRequiredFields { get; } = new();
        internal int ScannedEntryCount { get; set; }

        internal JObject ToJson() => new()
        {
            ["scanned_entry_count"] = ScannedEntryCount,
            ["duplicate_ids"] = new JArray(DuplicateIds.Cast<JToken>().ToArray()),
            ["duplicate_id_count"] = DuplicateIds.Count,
            ["dangling_references"] = new JArray(DanglingReferences.Cast<JToken>().ToArray()),
            ["dangling_reference_count"] = DanglingReferences.Count,
            ["entries_outside_resources"] = new JArray(EntriesOutsideResources.Cast<JToken>().ToArray()),
            ["entries_outside_resources_count"] = EntriesOutsideResources.Count,
            ["missing_required_fields"] = new JArray(MissingRequiredFields.Cast<JToken>().ToArray()),
            ["missing_required_fields_count"] = MissingRequiredFields.Count,
            ["total_issue_count"] = DuplicateIds.Count + DanglingReferences.Count
                + EntriesOutsideResources.Count + MissingRequiredFields.Count,
        };
    }

    internal static class DatabaseValidator
    {
        internal static ValidationReport Run()
        {
            var report = new ValidationReport();
            var guids = AssetDatabase.FindAssets("t:SparkDatabaseEntry");

            // First pass — collect entries, check missing-required and outside-resources.
            // Group by id for duplicate detection.
            var entriesById = new Dictionary<string, List<(SparkDatabaseEntry entry, string path)>>();
            var allEntries = new List<(SparkDatabaseEntry entry, string path)>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = AssetDatabase.LoadAssetAtPath<SparkDatabaseEntry>(path);
                if (entry == null) continue;
                allEntries.Add((entry, path));

                if (string.IsNullOrEmpty(entry.id))
                {
                    report.MissingRequiredFields.Add(new JObject
                    {
                        ["asset_path"] = path,
                        ["entry_type"] = entry.GetType().Name,
                        ["field"] = "id",
                        ["reason"] = "id is null or empty",
                    });
                }
                else
                {
                    if (!entriesById.TryGetValue(entry.id, out var list))
                    {
                        list = new List<(SparkDatabaseEntry, string)>();
                        entriesById[entry.id] = list;
                    }
                    list.Add((entry, path));
                }

                if (string.IsNullOrEmpty(entry.entryName))
                {
                    report.MissingRequiredFields.Add(new JObject
                    {
                        ["asset_path"] = path,
                        ["entry_type"] = entry.GetType().Name,
                        ["entry_id"] = entry.id,
                        ["field"] = "entryName",
                        ["reason"] = "entryName is null or empty",
                    });
                }

                if (!IsUnderResources(path))
                {
                    report.EntriesOutsideResources.Add(new JObject
                    {
                        ["asset_path"] = path,
                        ["entry_type"] = entry.GetType().Name,
                        ["entry_id"] = entry.id,
                        ["reason"] = "Asset path is not under a Resources/ folder — will not be included in builds.",
                    });
                }
            }

            // Duplicate ids — every group with > 1 entry.
            foreach (var kvp in entriesById)
            {
                if (kvp.Value.Count > 1)
                {
                    report.DuplicateIds.Add(new JObject
                    {
                        ["id"] = kvp.Key,
                        ["count"] = kvp.Value.Count,
                        ["entries"] = new JArray(kvp.Value.Select(e => (JToken)new JObject
                        {
                            ["asset_path"] = e.path,
                            ["entry_type"] = e.entry.GetType().Name,
                            ["entry_name"] = e.entry.entryName,
                        }).ToArray()),
                    });
                }
            }

            // Second pass — dangling references. For every entry, walk its SerializedObject
            // and check every SparkDatabaseEntry reference: is the referenced entry's id
            // registered? (We compare against the id field on the referenced object, not
            // against entriesById, so destroyed references that show as fake-null are
            // reported as dangling rather than missed.)
            var knownIds = new HashSet<string>(entriesById.Keys);
            foreach (var (entry, path) in allEntries)
            {
                using var so = new SerializedObject(entry);
                var prop = so.GetIterator();
                bool enterChildren = true;
                while (prop.Next(enterChildren))
                {
                    enterChildren = !ShouldSkipChildren(prop);
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;

                    var fieldRefersToDbEntry = prop.objectReferenceValue is SparkDatabaseEntry;
                    if (!fieldRefersToDbEntry) continue;

                    var refed = prop.objectReferenceValue as SparkDatabaseEntry;
                    if (refed == null)
                    {
                        // Fake-null — Unity reference exists in the file but the target is missing.
                        // We can't tell *what* id it used to point at; report the field as dangling.
                        report.DanglingReferences.Add(new JObject
                        {
                            ["referencing_asset_path"] = path,
                            ["referencing_entry_type"] = entry.GetType().Name,
                            ["referencing_entry_id"] = entry.id,
                            ["property_path"] = prop.propertyPath,
                            ["reason"] = "Object reference exists but target is missing (fake-null).",
                        });
                        continue;
                    }
                    if (!string.IsNullOrEmpty(refed.id) && !knownIds.Contains(refed.id))
                    {
                        report.DanglingReferences.Add(new JObject
                        {
                            ["referencing_asset_path"] = path,
                            ["referencing_entry_type"] = entry.GetType().Name,
                            ["referencing_entry_id"] = entry.id,
                            ["property_path"] = prop.propertyPath,
                            ["target_id"] = refed.id,
                            ["target_type"] = refed.GetType().Name,
                            ["reason"] = $"References id '{refed.id}' which has no entry in the registry.",
                        });
                    }
                }
            }

            report.ScannedEntryCount = allEntries.Count;
            return report;
        }

        private static bool IsUnderResources(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            var normalized = assetPath.Replace('\\', '/');
            return normalized.Contains("/Resources/");
        }

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
