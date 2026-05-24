/*
 * Plugin: Spark Tools MCP
 *
 * spark_get_entry — fetch the current state of a Spark database entry as JSON.
 * Mirrors the schema shape but with values populated. Used for inspecting an
 * existing entry before duplicating, generating variations, or batch-editing.
 *
 * Params:
 *   id (string, required) — entry ID as set by Spark's authoring layer
 *
 * Returns:
 *   - entry_type, owning_plugin, asset_path
 *   - fields: { fieldName: value } where value is the current SO field state.
 *     UnityEngine.Object references are serialized as their asset paths.
 *     SparkDatabaseEntry references are serialized as { id, type } pairs.
 *     Lists/arrays recurse. Anything we can't serialize cleanly is reported
 *     as { "_unserializable": true, "type_name": "..." } so the caller can
 *     decide what to do.
 */

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Common;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_get_entry",
        Description = "Fetch the current state of a Spark database entry as JSON. Returns entry_type, owning_plugin, asset_path, and fields (with values serialized: UnityEngine.Object refs as asset paths, SparkDatabaseEntry refs as {id, type}, lists/arrays recursed). Use before spark_duplicate_entry, spark_generate_variations, or spark_update_entry to know the current state.",
        Group = "core"
    )]
    public static class GetEntryHandler
    {
        private const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");
            var id = @params.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                return McpResult.Error("id is required.");

            try
            {
                var entry = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(id);
                if (entry == null)
                {
                    return McpResult.Error(
                        $"No SparkDatabaseEntry found with id '{id}'.",
                        new JObject { ["entry_count"] = SparkDatabaseRegistry.GetTotalEntryCount() });
                }

                var assetPath = AssetDatabase.GetAssetPath(entry);
                var fields = new JObject();
                foreach (var field in entry.GetType().GetFields(FieldFlags))
                {
                    if (field.IsStatic) continue;
                    object value;
                    try { value = field.GetValue(entry); }
                    catch (Exception ex)
                    {
                        fields[field.Name] = new JObject { ["_read_error"] = ex.Message };
                        continue;
                    }
                    fields[field.Name] = SerializeValue(value);
                }

                return McpResult.Success(new JObject
                {
                    ["id"] = entry.id,
                    ["entry_type"] = entry.GetType().Name,
                    ["entry_type_full_name"] = entry.GetType().FullName,
                    ["asset_path"] = assetPath,
                    ["fields"] = fields,
                });
            }
            catch (Exception ex)
            {
                return McpResult.Error($"spark_get_entry crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static JToken SerializeValue(object value)
        {
            if (value == null) return JValue.CreateNull();

            switch (value)
            {
                case string s:
                    return new JValue(s);
                case bool b:
                    return new JValue(b);
                case int i:
                    return new JValue(i);
                case long l:
                    return new JValue(l);
                case float f:
                    return new JValue(f);
                case double d:
                    return new JValue(d);
                case Enum e:
                    return new JValue(e.ToString());
            }

            // SparkDatabaseEntry → {id, type}
            if (value is SparkDatabaseEntry spark)
            {
                return new JObject
                {
                    ["id"] = spark.id,
                    ["entry_type"] = spark.GetType().Name,
                };
            }

            // UnityEngine.Object → asset path (or name if not an asset).
            // Unity has "fake null": a destroyed/unassigned Object reference is not C#-null
            // but `== null` (via the overloaded operator) returns true. Check both.
            if (value is UnityEngine.Object unityObj)
            {
                if (unityObj == null) return JValue.CreateNull();
                var path = AssetDatabase.GetAssetPath(unityObj);
                return new JObject
                {
                    ["asset_path"] = string.IsNullOrEmpty(path) ? null : path,
                    ["name"] = unityObj.name,
                    ["type"] = unityObj.GetType().Name,
                };
            }

            // IEnumerable (lists, arrays) — recurse
            if (value is IEnumerable enumerable && !(value is string))
            {
                var arr = new JArray();
                foreach (var item in enumerable) arr.Add(SerializeValue(item));
                return arr;
            }

            // Nested serializable struct/class — flatten its fields
            var t = value.GetType();
            if (t.IsValueType
                || t.GetCustomAttributes(typeof(SerializableAttribute), inherit: false).Length > 0)
            {
                var nested = new JObject { ["_struct_type"] = t.Name };
                foreach (var f in t.GetFields(FieldFlags))
                {
                    if (f.IsStatic) continue;
                    try { nested[f.Name] = SerializeValue(f.GetValue(value)); }
                    catch (Exception ex) { nested[f.Name] = new JObject { ["_read_error"] = ex.Message }; }
                }
                return nested;
            }

            return new JObject
            {
                ["_unserializable"] = true,
                ["type_name"] = t.FullName,
                ["to_string"] = value.ToString(),
            };
        }
    }
}
