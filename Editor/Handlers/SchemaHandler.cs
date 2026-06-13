/*
 * Plugin: Spark Tools MCP
 *
 * spark_schema — given a type name, returns its JSON schema descriptor: every
 * field name, type category, attribute metadata, and the canonical save path.
 * Works for both SparkDatabaseEntry subclasses (via PluginManifest) and
 * SparkDatabaseExtensionData subclasses (via PluginExtensionManifest).
 *
 * Params:
 *   entry_type (string, required) — e.g. "ItemEntry" or "ItemStatsExtensionData"
 *
 * On unknown type, returns an error with suggestions from both registries so
 * the caller can recover. The response includes a top-level `kind` field —
 * "database_entry" or "extension_data" — so the caller knows which authoring
 * tool to use (spark_create_entry vs spark_create_extension).
 */

using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using RockRabbit.SparkToolsMCP.SchemaIntrospection;
using UnityEditor;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_schema",
        Description = "Return the field-level schema for a Spark type — both SparkDatabaseEntry subclasses (e.g. ItemEntry) and SparkDatabaseExtensionData subclasses (e.g. ItemStatsExtensionData). Walks public instance fields (including inherited ones), classifies each by type category (primitive, string, enum, list, array, scriptable_object_reference, spark_database_entry_reference, unity_object_reference, nested_serializable), and includes Spark inspector attribute metadata (section, display_name, tooltip, writable, selector, nested_data, conditional, range, text_area). The response's `kind` field indicates whether to author with spark_create_entry or spark_create_extension.",
        Group = "core"
    )]
    public static class SchemaHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("Type name to describe, e.g. 'ItemEntry' (a SparkDatabaseEntry subclass) or 'ItemStatsExtensionData' (a SparkDatabaseExtensionData subclass).")]
            public string entry_type { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var entryType = @params.Value<string>("entry_type") ?? @params.Value<string>("entryType");
            if (string.IsNullOrWhiteSpace(entryType))
                return McpResult.Error("entry_type is required.");

            try
            {
                // First try as a SparkDatabaseEntry via PluginManifest.
                var entryResolution = PathResolver.Resolve(entryType);
                if (entryResolution != null)
                {
                    var schema = EntrySchemaBuilder.Build(
                        entryResolution.AssetType,
                        entryResolution.PluginName,
                        entryResolution.Tab.assetAsmdef,
                        entryResolution.Tab.path);
                    schema["kind"] = "database_entry";
                    return McpResult.Success(schema);
                }

                // Fall through to extension types.
                var extResolution = ExtensionPathResolver.Resolve(entryType);
                if (extResolution != null)
                {
                    var manifestPath = extResolution.Manifest != null
                        ? AssetDatabase.GetAssetPath(extResolution.Manifest)
                        : null;
                    var schema = EntrySchemaBuilder.Build(
                        extResolution.ExtensionType,
                        extResolution.ExtensionName ?? extResolution.Asmdef,
                        extResolution.Asmdef,
                        extResolution.SavePath);
                    schema["kind"] = "extension_data";
                    schema["has_manifest"] = extResolution.HasManifest;
                    schema["manifest_path"] = manifestPath;
                    schema["extension_name"] = extResolution.ExtensionName;
                    schema["version"] = extResolution.Version;
                    return McpResult.Success(schema);
                }

                return McpResult.Error(
                    $"No SparkDatabaseEntry or SparkDatabaseExtensionData type named '{entryType}'.",
                    new JObject { ["suggestions"] = SuggestSimilar(entryType) });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_schema crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static JArray SuggestSimilar(string query)
        {
            var lower = query.ToLowerInvariant();

            var entryNames = PathResolver.AllResolutions().Select(r => r.AssetType.Name);
            var extensionNames = ExtensionPathResolver.AllResolutions().Select(r => r.TypeName);
            var names = entryNames.Concat(extensionNames)
                .Where(n => n != null
                    && (n.ToLowerInvariant().Contains(lower) || lower.Contains(n.ToLowerInvariant())))
                .Distinct()
                .Take(5)
                .ToList();

            if (names.Count == 0)
            {
                names = entryNames.Concat(extensionNames)
                    .Where(n => n != null)
                    .OrderBy(n => n)
                    .Take(5)
                    .ToList();
            }

            return new JArray(names.Select(n => (JToken)n).ToArray());
        }
    }
}
