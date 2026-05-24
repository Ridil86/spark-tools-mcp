/*
 * Plugin: Spark Tools MCP
 *
 * Locate extension types and their canonical save paths.
 *
 * Spark stores extension-type metadata in PluginExtensionManifest ScriptableObjects
 * (one per extension type). The manifest carries:
 *   - targetTypeName  — the C# class name of the SparkDatabaseExtensionData subclass
 *   - savePath        — base folder (must be under a Resources/ path)
 *   - GetFullSavePath(targetId) → "{savePath}/{targetId}_{targetTypeName}.asset"
 *
 * This resolver:
 *   1. Walks all PluginExtensionManifest assets in the project
 *   2. Maps extension type name → (manifest, resolved Type)
 *   3. Enumerates every non-abstract SparkDatabaseExtensionData subclass loaded
 *      (regardless of whether a manifest exists for it) so spark_list_extension_types
 *      can show "type X exists but has no manifest"
 *
 * For path determination, the manifest is authoritative. We do not invent paths
 * for unmanifested types — the caller has to pass `path` explicitly in that case.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class ExtensionResolution
    {
        internal Type ExtensionType { get; }
        internal PluginExtensionManifest Manifest { get; }
        internal int AssetCount { get; }

        internal ExtensionResolution(Type extensionType, PluginExtensionManifest manifest, int assetCount)
        {
            ExtensionType = extensionType;
            Manifest = manifest;
            AssetCount = assetCount;
        }

        internal string TypeName => ExtensionType?.Name;
        internal string Asmdef => ExtensionType?.Assembly.GetName().Name;
        internal string SavePath => Manifest?.savePath;
        internal string ExtensionName => Manifest?.extensionName;
        internal string Version => Manifest?.version;
        internal bool HasManifest => Manifest != null;

        internal string ComputeAssetPath(string targetId, string pathOverride = null)
        {
            // Caller-provided path wins.
            if (!string.IsNullOrWhiteSpace(pathOverride))
            {
                var folder = pathOverride.Replace('\\', '/').TrimEnd('/');
                return $"{folder}/{targetId}_{TypeName}.asset";
            }

            if (Manifest != null)
            {
                return Manifest.GetFullSavePath(targetId);
            }

            return null;
        }
    }

    internal static class ExtensionPathResolver
    {
        internal static ExtensionResolution Resolve(string extensionTypeName)
        {
            if (string.IsNullOrWhiteSpace(extensionTypeName)) return null;

            var type = FindExtensionType(extensionTypeName);
            if (type == null) return null;

            var manifest = FindManifestForType(type);
            var assetCount = AssetDatabase.FindAssets($"t:{type.Name}")?.Length ?? 0;
            return new ExtensionResolution(type, manifest, assetCount);
        }

        /// <summary>
        /// Walk every non-abstract SparkDatabaseExtensionData subclass in any loaded
        /// assembly. Returns one resolution per type (including types without manifests).
        /// </summary>
        internal static IEnumerable<ExtensionResolution> AllResolutions()
        {
            var manifestByType = LoadAllManifests();
            foreach (var type in EnumerateExtensionTypes())
            {
                manifestByType.TryGetValue(type, out var manifest);
                var assetCount = AssetDatabase.FindAssets($"t:{type.Name}")?.Length ?? 0;
                yield return new ExtensionResolution(type, manifest, assetCount);
            }
        }

        private static Type FindExtensionType(string typeName)
        {
            foreach (var type in EnumerateExtensionTypes())
            {
                if (type.Name == typeName || type.FullName == typeName) return type;
            }
            return null;
        }

        private static IEnumerable<Type> EnumerateExtensionTypes()
        {
            var seen = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); } catch { continue; }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!typeof(SparkDatabaseExtensionData).IsAssignableFrom(type)) continue;
                    if (type == typeof(SparkDatabaseExtensionData)) continue;
                    if (seen.Add(type)) yield return type;
                }
            }
        }

        private static PluginExtensionManifest FindManifestForType(Type extensionType)
        {
            var guids = AssetDatabase.FindAssets("t:PluginExtensionManifest");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var manifest = AssetDatabase.LoadAssetAtPath<PluginExtensionManifest>(path);
                if (manifest == null) continue;
                var manifestType = manifest.TargetType;
                if (manifestType == extensionType) return manifest;
                if (manifestType == null
                    && (manifest.targetTypeName == extensionType.Name
                        || manifest.targetTypeName == extensionType.FullName))
                {
                    return manifest;
                }
            }
            return null;
        }

        private static Dictionary<Type, PluginExtensionManifest> LoadAllManifests()
        {
            var byType = new Dictionary<Type, PluginExtensionManifest>();
            var guids = AssetDatabase.FindAssets("t:PluginExtensionManifest");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var manifest = AssetDatabase.LoadAssetAtPath<PluginExtensionManifest>(path);
                if (manifest == null) continue;
                var type = manifest.TargetType;
                if (type != null && !byType.ContainsKey(type)) byType[type] = manifest;
            }
            return byType;
        }
    }
}
