/*
 * Plugin: Spark Tools MCP
 *
 * Given a SparkDatabaseEntry subclass Type, walk its public instance FieldInfo
 * (including inherited fields up to ScriptableObject) and emit a JSON Schema-ish
 * JObject describing each field's name, type, type category, and per-attribute
 * metadata.
 *
 * The output is *not* strict JSON Schema (no "$schema" header, no "required"
 * arrays) — it's a Spark-flavoured schema descriptor that callers can use to
 * author entries correctly without reading source. If we ever want true JSON
 * Schema later, this is the place that transforms.
 *
 * Type categories the schema reports:
 *   "primitive"                 — int, long, float, double, bool
 *   "string"
 *   "enum"                      — also includes enum_values and underlying_type
 *   "list"                      — also includes element_type + element_category
 *   "array"                     — same as list
 *   "spark_database_entry_reference" — references SparkDatabaseEntry by id
 *   "scriptable_object_reference"    — references a ScriptableObject (incl. *TypeBase)
 *   "unity_object_reference"         — Sprite, Material, GameObject, etc.
 *   "nested_serializable"            — a [Serializable] struct/class field (the lists of these are very common in Spark)
 *   "unknown"                        — anything we can't categorise
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.SchemaIntrospection
{
    internal static class EntrySchemaBuilder
    {
        private const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal static JObject Build(Type entryType, string owningPluginName, string asmdef, string defaultSavePath)
        {
            if (entryType == null)
                throw new ArgumentNullException(nameof(entryType));

            var result = new JObject
            {
                ["entry_type"] = entryType.Name,
                ["full_name"] = entryType.FullName,
                ["owning_plugin"] = owningPluginName,
                ["asmdef"] = asmdef,
                ["default_save_path"] = defaultSavePath,
                ["is_under_resources"] = !string.IsNullOrEmpty(defaultSavePath)
                    && (defaultSavePath.Replace('\\', '/').Contains("/Resources/")
                        || defaultSavePath.Replace('\\', '/').Contains("Resources/")),
                ["inheritance_chain"] = new JArray(InheritanceChain(entryType).Select(t => (JToken)t.Name).ToArray()),
            };

            var fieldsArr = new JArray();
            foreach (var (field, declaredBy) in WalkPublicInstanceFields(entryType))
            {
                fieldsArr.Add(DescribeField(field, declaredBy));
            }
            result["fields"] = fieldsArr;

            return result;
        }

        private static IEnumerable<Type> InheritanceChain(Type t)
        {
            var current = t;
            while (current != null && current != typeof(object))
            {
                yield return current;
                current = current.BaseType;
            }
        }

        private static IEnumerable<(FieldInfo field, Type declaredBy)> WalkPublicInstanceFields(Type t)
        {
            // Walk from the most-derived type up so the order in the output matches
            // what a reader of the .cs file would expect.
            var chain = new List<Type>();
            var current = t;
            while (current != null && current != typeof(ScriptableObject) && current != typeof(object))
            {
                chain.Add(current);
                current = current.BaseType;
            }
            chain.Reverse();   // base first → derived last

            foreach (var type in chain)
            {
                foreach (var field in type.GetFields(FieldFlags))
                {
                    if (field.IsStatic) continue;
                    yield return (field, type);
                }
            }
        }

        private static JObject DescribeField(FieldInfo field, Type declaredBy)
        {
            var meta = AttributeMetadataReader.Read(field);

            // Base shape — every field gets these keys.
            var entry = new JObject
            {
                ["name"] = field.Name,
                ["declared_by"] = declaredBy.Name,
                ["type"] = field.FieldType.Name,
                ["type_full_name"] = field.FieldType.FullName,
                ["writable"] = meta["writable"]?.Value<bool>() ?? true,
            };

            // Merge attribute metadata in (overwriting `writable` if [ReadOnly] said so).
            foreach (var prop in meta.Properties())
            {
                entry[prop.Name] = prop.Value;
            }

            // Type classification.
            ClassifyType(field.FieldType, entry);
            return entry;
        }

        private static void ClassifyType(Type type, JObject entry)
        {
            // Primitives & string
            if (type == typeof(string)) { entry["type_category"] = "string"; return; }
            if (type == typeof(bool) || type == typeof(int) || type == typeof(long)
                || type == typeof(float) || type == typeof(double) || type == typeof(byte)
                || type == typeof(short) || type == typeof(uint) || type == typeof(ulong))
            {
                entry["type_category"] = "primitive";
                entry["primitive_kind"] = type.Name.ToLowerInvariant();
                return;
            }

            // Enums
            if (type.IsEnum)
            {
                entry["type_category"] = "enum";
                entry["underlying_type"] = Enum.GetUnderlyingType(type).Name;
                entry["enum_values"] = new JArray(Enum.GetNames(type).Select(n => (JToken)n).ToArray());
                return;
            }

            // Lists & arrays
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                entry["type_category"] = "array";
                entry["element_type"] = elementType?.Name;
                entry["element_full_name"] = elementType?.FullName;
                if (elementType != null)
                {
                    var innerCategory = new JObject();
                    ClassifyType(elementType, innerCategory);
                    entry["element_category"] = innerCategory["type_category"];
                    if (innerCategory["enum_values"] != null) entry["element_enum_values"] = innerCategory["enum_values"];
                }
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                entry["type_category"] = "list";
                entry["element_type"] = elementType.Name;
                entry["element_full_name"] = elementType.FullName;
                var innerCategory = new JObject();
                ClassifyType(elementType, innerCategory);
                entry["element_category"] = innerCategory["type_category"];
                if (innerCategory["enum_values"] != null) entry["element_enum_values"] = innerCategory["enum_values"];
                if (innerCategory["element_type"] != null) entry["element_nested"] = innerCategory;
                return;
            }

            // ScriptableObject / SparkDatabaseEntry / UnityEngine.Object hierarchy.
            if (typeof(SparkDatabaseEntry).IsAssignableFrom(type))
            {
                entry["type_category"] = "spark_database_entry_reference";
                entry["target_type"] = type.Name;
                return;
            }
            if (typeof(ScriptableObject).IsAssignableFrom(type))
            {
                entry["type_category"] = "scriptable_object_reference";
                entry["target_type"] = type.Name;
                return;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                entry["type_category"] = "unity_object_reference";
                entry["target_type"] = type.Name;
                return;
            }

            // Nested serializable struct/class — Spark uses these heavily for sub-records
            // (AbilityCost, ApplyRulesData, AbilityAnimationPlayable, …).
            if (type.IsSerializable
                || type.GetCustomAttributes(typeof(SerializableAttribute), inherit: false).Length > 0
                || type.IsValueType)
            {
                entry["type_category"] = "nested_serializable";
                entry["target_type"] = type.Name;
                // We don't recurse — for v1 callers can issue spark_schema(nested_type) separately
                // if they need it. (Implemented via the same lookup but on the nested type.)
                return;
            }

            entry["type_category"] = "unknown";
            entry["target_type"] = type.Name;
        }
    }
}
