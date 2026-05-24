/*
 * Plugin: Spark Tools MCP
 *
 * Reflection-based assignment of a JObject fields dict onto a ScriptableObject.
 * Handles the common type coercion cases an MCP caller needs without forcing
 * them to know Unity-specific types:
 *
 *   - JSON primitives → C# primitives (int, long, float, double, bool, string)
 *   - JSON string → enum value (case-insensitive)
 *   - JSON string → UnityEngine.Object reference (resolved as an asset path,
 *     or — for SparkDatabaseEntry subclasses — as an entry ID via the registry)
 *   - JSON array → List<T> of any supported element type
 *   - JSON object → null (nested SOs are not supported in v1 — see TODO)
 *
 * Unsupported types log a warning and skip rather than throwing. Callers get
 * a list of which fields applied vs. which were skipped so they can correct
 * inputs in a retry.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal sealed class FieldSetResult
    {
        internal List<string> AppliedFields { get; } = new();
        internal List<JObject> SkippedFields { get; } = new();

        internal void RecordApplied(string fieldName) => AppliedFields.Add(fieldName);

        internal void RecordSkipped(string fieldName, string reason)
        {
            SkippedFields.Add(new JObject
            {
                ["field"] = fieldName,
                ["reason"] = reason,
            });
        }
    }

    internal static class FieldSetter
    {
        /// <summary>
        /// Apply each key in `fields` to the corresponding public field on `target`.
        /// Unknown fields and uncoercible values are recorded in the result rather
        /// than throwing — keeps batch operations from cratering on one bad input.
        /// </summary>
        internal static FieldSetResult Apply(object target, JObject fields)
        {
            var result = new FieldSetResult();
            if (target == null || fields == null) return result;

            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var fieldName = prop.Name;
                var fieldInfo = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (fieldInfo == null)
                {
                    result.RecordSkipped(fieldName, $"No public field '{fieldName}' on type {type.Name}");
                    continue;
                }

                if (TryCoerce(prop.Value, fieldInfo.FieldType, out var coerced, out var error))
                {
                    fieldInfo.SetValue(target, coerced);
                    result.RecordApplied(fieldName);
                }
                else
                {
                    result.RecordSkipped(fieldName, error);
                }
            }

            return result;
        }

        private static bool TryCoerce(JToken value, Type targetType, out object coerced, out string error)
        {
            coerced = null;
            error = null;

            if (value == null || value.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                {
                    error = $"Cannot assign null to value type {targetType.Name}";
                    return false;
                }
                coerced = null;
                return true;
            }

            // Direct primitive / string
            if (targetType == typeof(string)) { coerced = value.ToString(); return true; }
            if (targetType == typeof(int)) return TryNumeric<int>(value, out coerced, out error);
            if (targetType == typeof(long)) return TryNumeric<long>(value, out coerced, out error);
            if (targetType == typeof(float)) return TryNumeric<float>(value, out coerced, out error);
            if (targetType == typeof(double)) return TryNumeric<double>(value, out coerced, out error);
            if (targetType == typeof(bool))
            {
                try { coerced = value.ToObject<bool>(); return true; }
                catch (Exception ex) { error = $"Cannot coerce {value.Type} to bool: {ex.Message}"; return false; }
            }

            // Enums
            if (targetType.IsEnum)
            {
                var raw = value.ToString();
                if (Enum.TryParse(targetType, raw, ignoreCase: true, out var enumValue))
                {
                    coerced = enumValue;
                    return true;
                }
                error = $"'{raw}' is not a valid {targetType.Name} (expected one of: {string.Join(", ", Enum.GetNames(targetType))})";
                return false;
            }

            // UnityEngine.Object references — by asset path, or by entry ID for SparkDatabaseEntry
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                return TryResolveObjectReference(value, targetType, out coerced, out error);
            }

            // List<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return TryList(value, targetType, out coerced, out error);
            }

            // Arrays
            if (targetType.IsArray)
            {
                return TryArray(value, targetType, out coerced, out error);
            }

            // Nested objects (JObject → SerializeReference / inline SO) — not supported in v1
            if (value.Type == JTokenType.Object)
            {
                error = $"Nested object assignment to {targetType.Name} is not supported in v1. Author nested SOs separately and reference them by asset path.";
                return false;
            }

            // Last-ditch: let Newtonsoft try
            try
            {
                coerced = value.ToObject(targetType);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Cannot coerce {value.Type} to {targetType.Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryNumeric<T>(JToken value, out object coerced, out string error)
        {
            coerced = null;
            error = null;
            try
            {
                coerced = value.ToObject<T>();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Cannot coerce {value.Type} '{value}' to {typeof(T).Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryResolveObjectReference(JToken value, Type targetType, out object coerced, out string error)
        {
            coerced = null;
            error = null;

            var key = value.ToString();
            if (string.IsNullOrEmpty(key)) { coerced = null; return true; }

            // 1. Asset path (anything starting with Assets/)
            if (key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath(key, targetType);
                if (asset != null) { coerced = asset; return true; }
                error = $"No asset of type {targetType.Name} at path '{key}'";
                return false;
            }

            // 2. SparkDatabaseEntry ID lookup (most common case for cross-entry references)
            if (typeof(SparkDatabaseEntry).IsAssignableFrom(targetType))
            {
                var entry = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(key);
                if (entry != null && targetType.IsInstanceOfType(entry)) { coerced = entry; return true; }
                if (entry == null)
                {
                    error = $"No SparkDatabaseEntry with id '{key}'";
                    return false;
                }
                error = $"Entry '{key}' is of type {entry.GetType().Name}, not {targetType.Name}";
                return false;
            }

            // 3. GUID fallback (asset GUID)
            var guidPath = AssetDatabase.GUIDToAssetPath(key);
            if (!string.IsNullOrEmpty(guidPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath(guidPath, targetType);
                if (asset != null) { coerced = asset; return true; }
            }

            error = $"Could not resolve '{key}' as an asset path, entry ID, or GUID for type {targetType.Name}";
            return false;
        }

        private static bool TryList(JToken value, Type listType, out object coerced, out string error)
        {
            coerced = null;
            error = null;
            if (value.Type != JTokenType.Array)
            {
                error = $"Expected JSON array for List<{listType.GetGenericArguments()[0].Name}>, got {value.Type}";
                return false;
            }

            var elementType = listType.GetGenericArguments()[0];
            var listInstance = (IList)Activator.CreateInstance(listType);
            int index = 0;
            foreach (var item in (JArray)value)
            {
                if (!TryCoerce(item, elementType, out var elementCoerced, out var elementError))
                {
                    error = $"List element [{index}]: {elementError}";
                    return false;
                }
                listInstance.Add(elementCoerced);
                index++;
            }
            coerced = listInstance;
            return true;
        }

        private static bool TryArray(JToken value, Type arrayType, out object coerced, out string error)
        {
            coerced = null;
            error = null;
            if (value.Type != JTokenType.Array)
            {
                error = $"Expected JSON array for {arrayType.Name}, got {value.Type}";
                return false;
            }

            var elementType = arrayType.GetElementType();
            var jArr = (JArray)value;
            var arr = Array.CreateInstance(elementType!, jArr.Count);
            for (int i = 0; i < jArr.Count; i++)
            {
                if (!TryCoerce(jArr[i], elementType, out var elementCoerced, out var elementError))
                {
                    error = $"Array element [{i}]: {elementError}";
                    return false;
                }
                arr.SetValue(elementCoerced, i);
            }
            coerced = arr;
            return true;
        }
    }
}
