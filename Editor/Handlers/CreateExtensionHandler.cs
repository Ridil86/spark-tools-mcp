/*
 * Plugin: Spark Tools MCP
 *
 * spark_create_extension — create a new SparkDatabaseExtensionData asset
 * decorating an existing entry.
 *
 * Params:
 *   extension_type (string, required) — class name of the extension SO type
 *   target_id      (string, required) — id of the SparkDatabaseEntry being decorated
 *   fields         (object, optional) — additional field values applied via FieldSetter
 *   path           (string, optional) — override the manifest's save folder. Required
 *                                       when the extension type has no PluginExtensionManifest.
 *   overwrite      (bool,   optional) — default false. When false, an existing extension
 *                                       at the computed path is reported as an error.
 *
 * Returns: {extension_type, target_id, asset_path, applied_fields, skipped_fields,
 *           manifest_path}
 *
 * The save path is resolved from the type's PluginExtensionManifest unless overridden.
 * Filename is always `{target_id}_{ExtensionTypeName}.asset` — the convention Spark
 * uses for filename-based id inference in SparkExtensionRegistry.
 */

using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_create_extension",
        Description = "Create a new Spark extension data asset (SparkDatabaseExtensionData subclass) that decorates an existing entry with plugin-specific data — without modifying the target plugin. Resolves the save path from the type's PluginExtensionManifest. Filename follows Spark's convention `{target_id}_{ExtensionTypeName}.asset`. SetTargetId is called automatically. Pass `path` explicitly for types without a manifest, or to override the default. Refuses to overwrite an existing extension at the same path unless overwrite=true.",
        Group = "core"
    )]
    public static class CreateExtensionHandler
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var extensionType = @params.Value<string>("extension_type") ?? @params.Value<string>("extensionType");
            var targetId = @params.Value<string>("target_id") ?? @params.Value<string>("targetId");
            var pathOverride = @params.Value<string>("path");
            var overwrite = @params.Value<bool?>("overwrite") ?? false;
            var fields = @params["fields"] as JObject;

            if (string.IsNullOrWhiteSpace(extensionType))
                return McpResult.Error("extension_type is required.");
            if (string.IsNullOrWhiteSpace(targetId))
                return McpResult.Error("target_id is required.");

            try
            {
                var resolution = ExtensionPathResolver.Resolve(extensionType);
                if (resolution == null)
                {
                    return McpResult.Error(
                        $"No SparkDatabaseExtensionData subclass named '{extensionType}' is loaded. Call spark_list_extension_types to see what's available.");
                }

                // Validate the target entry actually exists.
                var targetEntry = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(targetId);
                if (targetEntry == null)
                {
                    return McpResult.Error(
                        $"target_id '{targetId}' does not correspond to any SparkDatabaseEntry. Create the entry first (spark_create_entry) or pass an existing id.");
                }

                var assetPath = resolution.ComputeAssetPath(targetId, pathOverride);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return McpResult.Error(
                        $"Cannot determine save path for extension type '{extensionType}'. This type has no PluginExtensionManifest — pass `path` explicitly.",
                        new JObject
                        {
                            ["extension_type"] = extensionType,
                            ["has_manifest"] = false,
                        });
                }

                // Refuse to clobber an existing asset unless explicitly told to.
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null && !overwrite)
                {
                    return McpResult.Error(
                        $"An asset already exists at '{assetPath}'. Pass overwrite=true to replace it.",
                        new JObject
                        {
                            ["asset_path"] = assetPath,
                            ["extension_type"] = extensionType,
                            ["target_id"] = targetId,
                        });
                }

                // Ensure the parent folder exists. AssetDatabase.CreateAsset requires it.
                var folder = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                {
                    if (!CreateFolderRecursive(folder))
                    {
                        return McpResult.Error(
                            $"Failed to create folder '{folder}'. Pass `path` to a folder that exists or that can be created under Assets/.");
                    }
                }

                // Instantiate, set the target id, then create the asset.
                var instance = ScriptableObject.CreateInstance(resolution.ExtensionType) as SparkDatabaseExtensionData;
                if (instance == null)
                {
                    return McpResult.Error($"ScriptableObject.CreateInstance returned null for type '{extensionType}'.");
                }
                instance.SetTargetId(targetId);
                instance.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);

                // If overwrite=true and an asset already exists, delete first.
                if (overwrite && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }

                AssetDatabase.CreateAsset(instance, assetPath);

                // Apply caller-supplied field overrides via FieldSetter.
                FieldSetResult applyResult = null;
                if (fields != null && fields.Count > 0)
                {
                    applyResult = FieldSetter.Apply(instance, fields);
                }

                EditorUtility.SetDirty(instance);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // SparkExtensionRegistry is editor-load-time; force a refresh so the new
                // extension is immediately queryable via GetExtensionData<T>().
                SparkExtensionRegistry.Refresh();

                var manifestPath = resolution.Manifest != null
                    ? AssetDatabase.GetAssetPath(resolution.Manifest)
                    : null;

                return McpResult.Success(new JObject
                {
                    ["extension_type"] = resolution.TypeName,
                    ["target_id"] = targetId,
                    ["asset_path"] = assetPath,
                    ["manifest_path"] = manifestPath,
                    ["has_manifest"] = resolution.HasManifest,
                    ["applied_fields"] = applyResult != null
                        ? new JArray(applyResult.AppliedFields)
                        : new JArray(),
                    ["skipped_fields"] = applyResult != null
                        ? new JArray(applyResult.SkippedFields.ConvertAll(o => (JToken)o).ToArray())
                        : new JArray(),
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_create_extension crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static bool CreateFolderRecursive(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            assetPath = assetPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetPath)) return true;
            if (!assetPath.StartsWith("Assets/") && assetPath != "Assets") return false;

            var parts = assetPath.Split('/');
            var current = parts[0];                  // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid)) return false;
                }
                current = next;
            }
            return AssetDatabase.IsValidFolder(assetPath);
        }
    }
}
