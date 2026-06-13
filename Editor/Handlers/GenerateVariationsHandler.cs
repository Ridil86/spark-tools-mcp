/*
 * Plugin: Spark Tools MCP
 *
 * spark_generate_variations — produce N variations of a template by scaling one
 * numeric field across a curve. This is the headline tool: 10 weapon tiers
 * scaling damage linearly from 5 to 50 should take one call.
 *
 * Params:
 *   template_id (string, required)
 *   axis        (string, required) — field name on the template to vary
 *                                    (v1: top-level fields only, no dotted paths)
 *   count       (int, required, >= 1)
 *   scaling     (object, required)
 *       type: "linear" | "exp"          (exp = geometric/multiplicative interpolation)
 *       from: number                    (value at i=0 / first variation)
 *       to:   number                    (value at i=count-1 / last variation)
 *   naming      (object, optional)
 *       suffix:       string with {n} for 1-indexed position. Default "_t{n}".
 *       display_name: string with {n} and {base}. Default "{base} Tier {n}".
 *       (Variation entry_name = source.entryName + suffix.)
 *   extra_overrides (object, optional)
 *       Extra field→value overrides applied to every variation in addition to the axis.
 *
 * Returns the same shape as spark_batch_create_from_template, plus the
 * computed_values array so callers can verify the scaling math.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using RockRabbit.SparkToolsMCP.Authoring;
using RockRabbit.SparkToolsMCP.Common;

namespace RockRabbit.SparkToolsMCP.Handlers
{
    [McpForUnityTool(
        "spark_generate_variations",
        Description = "Generate N stat-scaled variations of a template Spark entry by interpolating one numeric field. Supports linear and exponential (geometric) scaling between a 'from' and 'to' value. Each variation gets a unique entry_name and displayName via {n} (1-indexed position) and {base} (source displayName) interpolation. Returns the batch result plus the computed scaling values so you can verify the math. The killer use case: 'generate 10 weapon tiers of iron_sword scaling damage from 5 to 50 linearly'.",
        Group = "core"
    )]
    public static class GenerateVariationsHandler
    {
        // MCP input schema. Unity MCP's ToolDiscoveryService reflects [ToolParameter]
        // properties off this nested "Parameters" type; the property name becomes the
        // JSON-schema key verbatim, so names are snake_case to match the @params reads.
        public class Parameters
        {
            [ToolParameter("ID of the template entry to vary.")]
            public string template_id { get; set; }

            [ToolParameter("Name of the numeric field to interpolate across variations.")]
            public string axis { get; set; }

            [ToolParameter("Number of variations to generate (>= 1).")]
            public int count { get; set; }

            [ToolParameter("Scaling spec, e.g. {\"type\":\"linear\",\"from\":5,\"to\":50}. type is 'linear' or 'exp'.")]
            public object scaling { get; set; }

            [ToolParameter("Optional naming templates, e.g. {\"suffix\":\"_t{n}\",\"display_name\":\"{base} Tier {n}\"}. {n} is the 1-indexed position, {base} is the source displayName.", Required = false)]
            public object naming { get; set; }

            [ToolParameter("Optional object of additional field -> value overrides applied to every variation.", Required = false)]
            public object extra_overrides { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return McpResult.Error("Missing parameters.");

            var templateId = @params.Value<string>("template_id") ?? @params.Value<string>("templateId");
            if (string.IsNullOrWhiteSpace(templateId))
                return McpResult.Error("template_id is required.");

            var axis = @params.Value<string>("axis");
            if (string.IsNullOrWhiteSpace(axis))
                return McpResult.Error("axis (the field name to vary) is required.");

            var count = @params["count"]?.Value<int?>() ?? 0;
            if (count < 1) return McpResult.Error("count is required and must be >= 1.");

            var scaling = @params["scaling"] as JObject;
            if (scaling == null) return McpResult.Error("scaling is required (e.g. {\"type\":\"linear\",\"from\":5,\"to\":50}).");

            var scalingType = (scaling.Value<string>("type") ?? "linear").ToLowerInvariant();
            if (scalingType != "linear" && scalingType != "exp")
                return McpResult.Error($"Unknown scaling.type '{scalingType}'. v1 supports: linear, exp.");

            double from = scaling.Value<double?>("from") ?? 0.0;
            double to = scaling.Value<double?>("to") ?? 0.0;
            if (scalingType == "exp" && (from <= 0 || to <= 0))
                return McpResult.Error("Exponential scaling requires from and to > 0.");

            var naming = @params["naming"] as JObject ?? new JObject();
            var suffixTemplate = naming.Value<string>("suffix") ?? "_t{n}";
            var displayNameTemplate = naming.Value<string>("display_name") ?? naming.Value<string>("displayName") ?? "{base} Tier {n}";

            var extraOverrides = @params["extra_overrides"] as JObject ?? @params["extraOverrides"] as JObject;

            try
            {
                var source = SparkDatabaseRegistry.GetEntry<SparkDatabaseEntry>(templateId);
                if (source == null)
                    return McpResult.Error($"No template entry with id '{templateId}'.");

                // Validate axis field exists and is numeric.
                var axisField = source.GetType().GetField(axis,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (axisField == null)
                    return McpResult.Error($"Field '{axis}' not found on {source.GetType().Name}. Call spark_schema('{source.GetType().Name}') to see available fields.");
                if (!IsNumericType(axisField.FieldType))
                    return McpResult.Error($"Field '{axis}' on {source.GetType().Name} has type {axisField.FieldType.Name} which is not numeric. Variations require a numeric axis (int, long, float, double).");

                var baseDisplayName = source.displayName ?? source.entryName ?? source.id ?? "Template";
                var baseEntryName = source.entryName ?? source.displayName ?? source.id ?? "template";

                var variations = new JArray();
                var computedValues = new JArray();

                for (int i = 0; i < count; i++)
                {
                    double t = count == 1 ? 0.0 : (double)i / (count - 1);   // 0..1 inclusive
                    double value = scalingType == "linear"
                        ? Lerp(from, to, t)
                        : ExpLerp(from, to, t);

                    var coercedValue = CoerceForFieldType(value, axisField.FieldType);
                    var oneBased = (i + 1).ToString(CultureInfo.InvariantCulture);

                    var overrides = new JObject();
                    if (extraOverrides != null)
                    {
                        foreach (var p in extraOverrides.Properties()) overrides[p.Name] = p.Value;
                    }
                    overrides[axis] = JToken.FromObject(coercedValue);
                    var resolvedDisplay = displayNameTemplate.Replace("{n}", oneBased).Replace("{base}", baseDisplayName);
                    overrides["displayName"] = resolvedDisplay;

                    var resolvedSuffix = suffixTemplate.Replace("{n}", oneBased).Replace("{base}", baseEntryName);
                    var variationEntryName = baseEntryName + resolvedSuffix;

                    variations.Add(new JObject
                    {
                        ["entry_name"] = variationEntryName,
                        ["overrides"] = overrides,
                    });
                    computedValues.Add(new JObject
                    {
                        ["index"] = i,
                        ["entry_name"] = variationEntryName,
                        ["value"] = JToken.FromObject(coercedValue),
                    });
                }

                // Delegate to the same plumbing batch_create_from_template uses.
                var created = new List<JObject>();
                var failed = new List<JObject>();
                int idx = 0;
                foreach (var v in variations)
                {
                    var variation = (JObject)v;
                    var entryName = variation.Value<string>("entry_name");
                    var overrides = variation["overrides"] as JObject;
                    var result = DuplicationFacade.Duplicate(templateId, entryName, overrides);
                    if (result.Success) created.Add(result.ToData());
                    else failed.Add(new JObject { ["index"] = idx, ["entry_name"] = entryName, ["error"] = result.Error });
                    idx++;
                }

                return McpResult.Success(new JObject
                {
                    ["template_id"] = templateId,
                    ["template_entry_type"] = source.GetType().Name,
                    ["axis"] = axis,
                    ["scaling_type"] = scalingType,
                    ["from"] = from,
                    ["to"] = to,
                    ["count"] = count,
                    ["created_count"] = created.Count,
                    ["failed_count"] = failed.Count,
                    ["computed_values"] = computedValues,
                    ["created"] = new JArray(created.Cast<JToken>().ToArray()),
                    ["failed"] = new JArray(failed.Cast<JToken>().ToArray()),
                });
            }
            catch (Exception ex)
            {
                return McpResult.Error($"spark_generate_variations crashed: {ex.Message}", new JObject
                {
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace,
                });
            }
        }

        private static bool IsNumericType(Type t)
        {
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte)
                || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double ExpLerp(double a, double b, double t)
        {
            // Geometric interpolation: a * (b/a)^t. Preserves the ratio "from→to" across steps.
            return a * Math.Pow(b / a, t);
        }

        private static object CoerceForFieldType(double value, Type target)
        {
            if (target == typeof(int)) return (int)Math.Round(value);
            if (target == typeof(long)) return (long)Math.Round(value);
            if (target == typeof(short)) return (short)Math.Round(value);
            if (target == typeof(byte)) return (byte)Math.Round(value);
            if (target == typeof(uint)) return (uint)Math.Round(value);
            if (target == typeof(ulong)) return (ulong)Math.Round(value);
            if (target == typeof(float)) return (float)value;
            if (target == typeof(double)) return value;
            if (target == typeof(decimal)) return (decimal)value;
            return value;
        }
    }
}
