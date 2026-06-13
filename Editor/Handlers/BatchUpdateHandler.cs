/*
 * Plugin: Spark Tools MCP
 *
 * spark_batch_update — apply field_changes to every entry matching a filter.
 * The same expression syntax as spark_update_entry applies: +N, -N, *N, /N, =N
 * on numeric fields modify each entry's current value independently.
 *
 * Params:
 *   filter (object, required) — at least one of:
 *     entry_type  (string)        — restrict to one SparkDatabaseEntry subclass
 *     ids         (array<string>) — restrict to specific ids
 *     substring   (string)        — case-insensitive substring match on
 *                                   id / entryName / displayName
 *   field_changes (object, required) — {fieldName: value-or-expression}
 *
 * Safety: the filter must contain at least one criterion. An empty filter
 * (which would match every entry in the database) is rejected up front. Use
 * a broad filter like {"entry_type": "ItemEntry"} to intentionally target an
 * entire type.
 *
 * Returns: {matched_count, updated_count, skipped_count, updated: [...], skipped: [...]}
 */

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using RockRabbit.SparkToolsMCP.Validation;
using UnityEditor;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_batch_update",
        Description = "Apply the same field_changes to every Spark entry matching a filter. The filter requires at least one criterion (entry_type, ids, or substring) — an empty filter is rejected to prevent accidental whole-database mutations. Arithmetic expressions (+N, -N, *N, /N, =N) on numeric fields are resolved per-entry against that entry's current value. Use this for 'multiply damage by 1.1 on all fire-damage items' style operations.",
        Group = "core"
    )]
    public static class BatchUpdateHandler
    {
        private const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance;

        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("Selection criteria object; provide at least one of entry_type (string), ids (array of strings), or substring (string).")]
            public object filter { get; set; }

            [ToolParameter("Object of {fieldName: value-or-expression} changes applied to every matched entry (same expression syntax as spark_update_entry).")]
            public object field_changes { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var filter = @params["filter"] as JObject;
            if (filter == null || filter.Count == 0)
                return McpResult.Error("filter is required and must contain at least one criterion (entry_type, ids, or substring).");

            var fieldChanges = @params["field_changes"] as JObject ?? @params["fieldChanges"] as JObject;
            if (fieldChanges == null || fieldChanges.Count == 0)
                return McpResult.Error("field_changes is required and must contain at least one entry.");

            var entryTypeName = filter.Value<string>("entry_type") ?? filter.Value<string>("entryType");
            var idsToken = filter["ids"] as JArray;
            var substring = filter.Value<string>("substring");
            if (string.IsNullOrWhiteSpace(entryTypeName) && (idsToken == null || idsToken.Count == 0) && string.IsNullOrWhiteSpace(substring))
                return McpResult.Error("filter must have at least one of: entry_type, ids, substring.");

            try
            {
                var entries = ResolveFilter(entryTypeName, idsToken, substring);

                var updated = new List<JObject>();
                var skipped = new List<JObject>();

                foreach (var entry in entries)
                {
                    if (entry == null) continue;

                    var resolved = ExpressionEvaluator.ResolveExpressions(fieldChanges, entry);
                    var apply = FieldSetter.Apply(entry, resolved);
                    if (apply.AppliedFields.Count > 0)
                    {
                        EditorUtility.SetDirty(entry);
                        AssetDatabase.SaveAssetIfDirty(entry);
                        updated.Add(new JObject
                        {
                            ["id"] = entry.id,
                            ["entry_type"] = entry.GetType().Name,
                            ["asset_path"] = AssetDatabase.GetAssetPath(entry),
                            ["applied_fields"] = new JArray(apply.AppliedFields),
                            ["skipped_fields"] = new JArray(apply.SkippedFields.ConvertAll(o => (JToken)o).ToArray()),
                        });
                    }
                    else
                    {
                        skipped.Add(new JObject
                        {
                            ["id"] = entry.id,
                            ["entry_type"] = entry.GetType().Name,
                            ["reason"] = "No fields applied. Check field names against spark_schema('" + entry.GetType().Name + "').",
                            ["skipped_fields"] = new JArray(apply.SkippedFields.ConvertAll(o => (JToken)o).ToArray()),
                        });
                    }
                }

                return McpResult.Success(new JObject
                {
                    ["matched_count"] = entries.Count,
                    ["updated_count"] = updated.Count,
                    ["skipped_count"] = skipped.Count,
                    ["updated"] = new JArray(updated.Cast<JToken>().ToArray()),
                    ["skipped"] = new JArray(skipped.Cast<JToken>().ToArray()),
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_batch_update crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static List<SparkDatabaseEntry> ResolveFilter(string entryTypeName, JArray idsToken, string substring)
        {
            // Start with the broadest set: either GetAllEntries<T>() for a known type,
            // or every SparkDatabaseEntry in the project.
            IEnumerable<SparkDatabaseEntry> baseSet;
            if (!string.IsNullOrWhiteSpace(entryTypeName))
            {
                var resolution = PathResolver.Resolve(entryTypeName);
                if (resolution == null || !typeof(SparkDatabaseEntry).IsAssignableFrom(resolution.AssetType))
                {
                    return new List<SparkDatabaseEntry>();
                }
                var method = typeof(SparkDatabaseRegistry).GetMethod(
                    nameof(SparkDatabaseRegistry.GetAllEntries),
                    BindingFlags.Public | BindingFlags.Static);
                var generic = method.MakeGenericMethod(resolution.AssetType);
                var list = generic.Invoke(null, null) as IEnumerable;
                baseSet = list.Cast<SparkDatabaseEntry>();
            }
            else
            {
                baseSet = EnumerateAllEntries();
            }

            // Then apply ids filter (intersection) and substring filter (intersection).
            HashSet<string> idsFilter = null;
            if (idsToken != null && idsToken.Count > 0)
            {
                idsFilter = new HashSet<string>(idsToken.Select(t => t.ToString()));
            }

            var lowerSubstring = string.IsNullOrWhiteSpace(substring) ? null : substring.ToLowerInvariant();

            var result = new List<SparkDatabaseEntry>();
            foreach (var entry in baseSet)
            {
                if (entry == null) continue;
                if (idsFilter != null && !idsFilter.Contains(entry.id)) continue;
                if (lowerSubstring != null && !MatchesSubstring(entry, lowerSubstring)) continue;
                result.Add(entry);
            }
            return result;
        }

        private static IEnumerable<SparkDatabaseEntry> EnumerateAllEntries()
        {
            var guids = AssetDatabase.FindAssets("t:SparkDatabaseEntry");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = AssetDatabase.LoadAssetAtPath<SparkDatabaseEntry>(path);
                if (entry != null) yield return entry;
            }
        }

        private static bool MatchesSubstring(SparkDatabaseEntry entry, string lower)
        {
            if (!string.IsNullOrEmpty(entry.id) && entry.id.ToLowerInvariant().Contains(lower)) return true;
            if (!string.IsNullOrEmpty(entry.entryName) && entry.entryName.ToLowerInvariant().Contains(lower)) return true;
            if (!string.IsNullOrEmpty(entry.displayName) && entry.displayName.ToLowerInvariant().Contains(lower)) return true;
            return false;
        }
    }
}
