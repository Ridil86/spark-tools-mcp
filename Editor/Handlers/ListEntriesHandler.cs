/*
 * Plugin: Spark Tools MCP
 *
 * spark_list_entries — list entries optionally filtered by type and a substring
 * query against id / entryName / displayName.
 *
 * Params:
 *   entry_type (string, optional) — restrict to one SparkDatabaseEntry subclass
 *   filter     (string, optional) — case-insensitive substring; matches against
 *                                   id, entryName, displayName
 *   limit      (int, optional)    — cap the response size (default 200)
 *
 * Returns: {entry_count, returned_count, entries: [{id, entry_type, entry_name,
 * display_name, asset_path}, ...]}
 *
 * Without filters this can return hundreds of rows quickly, so always uses a
 * default limit. Caller can raise it explicitly if needed.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_list_entries",
        Description = "List Spark database entries, optionally filtered by type and/or a substring query. Without filters returns up to `limit` entries (default 200). Each row: {id, entry_type, entry_name, display_name, asset_path}. Use this before spark_create_entry to check whether an id is taken, before spark_duplicate_entry to find a template, or for general exploration.",
        Group = "core"
    )]
    public static class ListEntriesHandler
    {
        private const int DefaultLimit = 200;

        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("Optional: restrict to one SparkDatabaseEntry subclass by name.", Required = false)]
            public string entry_type { get; set; }

            [ToolParameter("Optional: case-insensitive substring matched against id, entryName, and displayName.", Required = false)]
            public string filter { get; set; }

            [ToolParameter("Optional: cap the number of rows returned (default 200).", Required = false)]
            public int limit { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            try
            {
                var entryTypeName = @params?.Value<string>("entry_type") ?? @params?.Value<string>("entryType");
                var filterRaw = @params?.Value<string>("filter");
                var limit = @params?.Value<int?>("limit") ?? DefaultLimit;
                if (limit <= 0) limit = DefaultLimit;

                var filter = string.IsNullOrWhiteSpace(filterRaw) ? null : filterRaw.ToLowerInvariant();

                Type targetType = null;
                if (!string.IsNullOrWhiteSpace(entryTypeName))
                {
                    var resolution = PathResolver.Resolve(entryTypeName);
                    if (resolution == null)
                    {
                        return McpResult.Error($"Unknown entry_type '{entryTypeName}'. Call spark_list_entry_types to see what's available.");
                    }
                    targetType = resolution.AssetType;
                }

                var entries = EnumerateEntries(targetType).ToList();
                int totalMatched = 0;
                var rows = new List<JObject>();

                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (filter != null && !MatchesFilter(entry, filter)) continue;
                    totalMatched++;
                    if (rows.Count < limit)
                    {
                        rows.Add(new JObject
                        {
                            ["id"] = entry.id,
                            ["entry_type"] = entry.GetType().Name,
                            ["entry_name"] = entry.entryName,
                            ["display_name"] = entry.displayName,
                            ["asset_path"] = AssetDatabase.GetAssetPath(entry),
                        });
                    }
                }

                return McpResult.Success(new JObject
                {
                    ["entry_count"] = totalMatched,
                    ["returned_count"] = rows.Count,
                    ["limit"] = limit,
                    ["truncated"] = totalMatched > rows.Count,
                    ["entries"] = new JArray(rows.Cast<JToken>().ToArray()),
                });
            }
            catch (Exception ex)
            {
                return McpResult.Error($"spark_list_entries crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static IEnumerable<SparkDatabaseEntry> EnumerateEntries(Type targetType)
        {
            if (targetType != null)
            {
                // Use SparkDatabaseRegistry.GetAllEntries<T> reflectively for the
                // type-filtered path — same trick ListEntryTypesHandler uses.
                var method = typeof(SparkDatabaseRegistry).GetMethod(
                    nameof(SparkDatabaseRegistry.GetAllEntries),
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null && typeof(SparkDatabaseEntry).IsAssignableFrom(targetType))
                {
                    var generic = method.MakeGenericMethod(targetType);
                    var list = generic.Invoke(null, null) as IEnumerable;
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            if (item is SparkDatabaseEntry e) yield return e;
                        }
                    }
                    yield break;
                }
            }

            // No type filter → walk every SparkDatabaseEntry asset in the project.
            var guids = AssetDatabase.FindAssets("t:SparkDatabaseEntry");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = AssetDatabase.LoadAssetAtPath<SparkDatabaseEntry>(path);
                if (entry != null) yield return entry;
            }
        }

        private static bool MatchesFilter(SparkDatabaseEntry entry, string lowerFilter)
        {
            if (!string.IsNullOrEmpty(entry.id) && entry.id.ToLowerInvariant().Contains(lowerFilter)) return true;
            if (!string.IsNullOrEmpty(entry.entryName) && entry.entryName.ToLowerInvariant().Contains(lowerFilter)) return true;
            if (!string.IsNullOrEmpty(entry.displayName) && entry.displayName.ToLowerInvariant().Contains(lowerFilter)) return true;
            return false;
        }
    }
}
