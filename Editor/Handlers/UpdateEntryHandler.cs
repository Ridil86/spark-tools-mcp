/*
 * Plugin: Spark Tools MCP
 *
 * spark_update_entry — patch specific fields on an existing entry.
 *
 * Params:
 *   id     (string, required)
 *   fields (object, required) — {fieldName: value-or-expression}
 *     Numeric field values may use the arithmetic expressions "+N", "-N",
 *     "*N", "/N", "=N" to modify based on current values (see
 *     ExpressionEvaluator). Non-numeric fields use direct assignment.
 *
 * Returns: {id, asset_path, entry_type, applied_fields, skipped_fields,
 *           previous_values}
 *
 * previous_values is a small snapshot of just the fields that actually changed,
 * so callers (or a future undo layer) can verify the delta.
 */

using System.Collections.Generic;
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
        "spark_update_entry",
        Description = "Patch specific fields on an existing Spark database entry. Supports arithmetic expressions on numeric fields: '+5' / '-3' / '*1.1' / '/2' / '=10' modify the current value (anything else is direct assignment). Returns the applied field set, anything skipped (with reason), and a previous_values snapshot of the changed fields. Use spark_batch_update for the same change across many entries.",
        Group = "core"
    )]
    public static class UpdateEntryHandler
    {
        private const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var id = @params.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return McpResult.Error("id is required.");

            var fields = @params["fields"] as JObject;
            if (fields == null || fields.Count == 0)
                return McpResult.Error("fields is required and must contain at least one entry.");

            try
            {
                var entry = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(id);
                if (entry == null)
                    return McpResult.Error($"No SparkDatabaseEntry with id '{id}'.");

                // Snapshot current values of every field we're about to touch so we
                // can report previous_values for whatever actually changes.
                var previousValues = new JObject();
                foreach (var prop in fields.Properties())
                {
                    var field = entry.GetType().GetField(prop.Name, FieldFlags);
                    if (field == null) continue;
                    try { previousValues[prop.Name] = field.GetValue(entry) is { } v ? JToken.FromObject(v) : JValue.CreateNull(); }
                    catch { previousValues[prop.Name] = JValue.CreateNull(); }
                }

                var resolvedFields = ExpressionEvaluator.ResolveExpressions(fields, entry);
                var applyResult = FieldSetter.Apply(entry, resolvedFields);

                if (applyResult.AppliedFields.Count > 0)
                {
                    EditorUtility.SetDirty(entry);
                    AssetDatabase.SaveAssetIfDirty(entry);
                }

                // Only keep previous_values for fields that actually applied.
                var changedPreviousValues = new JObject();
                foreach (var name in applyResult.AppliedFields)
                {
                    if (previousValues[name] != null) changedPreviousValues[name] = previousValues[name];
                }

                return McpResult.Success(new JObject
                {
                    ["id"] = entry.id,
                    ["asset_path"] = AssetDatabase.GetAssetPath(entry),
                    ["entry_type"] = entry.GetType().Name,
                    ["applied_fields"] = new JArray(applyResult.AppliedFields),
                    ["skipped_fields"] = new JArray(applyResult.SkippedFields.ConvertAll(o => (JToken)o).ToArray()),
                    ["previous_values"] = changedPreviousValues,
                });
            }
            catch (System.Exception ex)
            {
                return McpResult.Error($"spark_update_entry crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }
    }
}
