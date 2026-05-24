/*
 * Plugin: Spark Tools MCP
 *
 * Resolves where to save a new SparkDatabaseEntry by entry-type name. The
 * canonical answer comes from the project's PluginManifest assets — each
 * declares its tabs, and each tab carries an `assetType` (class name),
 * `assetAsmdef` (assembly name), and `path` (save folder).
 *
 * This class iterates every PluginManifest in the project on demand. The
 * lookup is cheap enough for v1 that we don't cache; if it becomes a
 * hotspot we can add a static cache keyed by entry-type name.
 *
 * Reference for the manifest shape:
 *   Assets/Blink/Spark/Core/Runtime/Plugins/PluginManifest.cs
 *   - GetAllTabs() at line 107
 *   - TabConfig.AssetType at line 81 (reflection-based type resolver)
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class TabResolution
    {
        internal PluginManifest Manifest { get; }
        internal PluginManifest.TabConfig Tab { get; }
        internal Type AssetType { get; }

        internal TabResolution(PluginManifest manifest, PluginManifest.TabConfig tab, Type assetType)
        {
            Manifest = manifest;
            Tab = tab;
            AssetType = assetType;
        }

        internal string SavePath => Tab.path;
        internal string PluginName => Manifest.pluginName;
    }

    internal static class PathResolver
    {
        /// <summary>
        /// Find the TabConfig that owns the given entry type. Match is by Type identity
        /// (we walk every manifest, evaluate TabConfig.AssetType, and compare to the
        /// requested type). Returns null if no manifest declares this type.
        /// </summary>
        internal static TabResolution Resolve(Type entryType)
        {
            if (entryType == null) return null;

            foreach (var manifest in LoadAllManifests())
            {
                if (manifest == null) continue;
                foreach (var tab in manifest.GetAllTabs())
                {
                    if (tab == null) continue;
                    var resolvedType = tab.AssetType;
                    if (resolvedType == null) continue;
                    if (resolvedType == entryType)
                    {
                        return new TabResolution(manifest, tab, resolvedType);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// String-keyed lookup. Matches against TabConfig.assetType (class name) first,
        /// then falls back to walking AssetType for full-name comparison. Helpful
        /// when callers pass "ItemEntry" without knowing the namespace.
        /// </summary>
        internal static TabResolution Resolve(string entryTypeName)
        {
            if (string.IsNullOrWhiteSpace(entryTypeName)) return null;

            TabResolution fallback = null;

            foreach (var manifest in LoadAllManifests())
            {
                if (manifest == null) continue;
                foreach (var tab in manifest.GetAllTabs())
                {
                    if (tab == null) continue;

                    // Fast path: string match on the manifest's declared assetType
                    if (string.Equals(tab.assetType, entryTypeName, StringComparison.Ordinal)
                        || (tab.AssetType != null && tab.AssetType.FullName == entryTypeName))
                    {
                        var t = tab.AssetType;
                        if (t != null) return new TabResolution(manifest, tab, t);
                    }

                    // Slow path: case-insensitive match — record as fallback only
                    if (fallback == null
                        && tab.AssetType != null
                        && string.Equals(tab.AssetType.Name, entryTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        fallback = new TabResolution(manifest, tab, tab.AssetType);
                    }
                }
            }

            return fallback;
        }

        /// <summary>
        /// Enumerate every TabConfig in the project, flat. Used by spark_list_entry_types.
        /// </summary>
        internal static IEnumerable<TabResolution> AllResolutions()
        {
            foreach (var manifest in LoadAllManifests())
            {
                if (manifest == null) continue;
                foreach (var tab in manifest.GetAllTabs())
                {
                    if (tab == null) continue;
                    var t = tab.AssetType;
                    if (t == null) continue;
                    yield return new TabResolution(manifest, tab, t);
                }
            }
        }

        /// <summary>
        /// True if the given path string lives somewhere under a Resources/ folder.
        /// Spark's runtime catalog discovery requires this for build inclusion.
        /// </summary>
        internal static bool IsUnderResources(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            var normalized = assetPath.Replace('\\', '/');
            return normalized.Contains("/Resources/") || normalized.StartsWith("Resources/");
        }

        private static IEnumerable<PluginManifest> LoadAllManifests()
        {
            var guids = AssetDatabase.FindAssets("t:PluginManifest");
            return guids
                .Select(g => AssetDatabase.LoadAssetAtPath<PluginManifest>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null);
        }
    }
}
