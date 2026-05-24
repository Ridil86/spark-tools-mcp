/*
 * Plugin: Spark Tools MCP
 *
 * Pre-process a fields JObject so string values like "+5", "*1.1", "=10"
 * on numeric fields get resolved against the target's current field value.
 *
 * Supported expression prefixes (string values only, numeric target fields only):
 *   "+N"  → current + N
 *   "-N"  → current - N
 *   "*N"  → current * N
 *   "/N"  → current / N   (division by zero is a no-op; keeps the current value)
 *   "=N"  → N             (same as just passing the number, but explicit)
 *
 * Anything not matching this pattern passes through unchanged so FieldSetter
 * still gets a shot at coercing it. The intent is "interpret arithmetic on
 * numeric fields, leave everything else alone."
 *
 * This is intentionally tiny. nCalc or a real expression DSL would be overkill
 * for v1 — most batch updates are "tweak this stat by ±X% across N entries."
 */

using System;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RockRabbit.SparkToolsMCP.Validation
{
    internal static class ExpressionEvaluator
    {
        private const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// Returns a new JObject where string arithmetic expressions on numeric
        /// fields have been resolved to literal numbers using <paramref name="target"/>'s
        /// current values. The original is not mutated.
        /// </summary>
        internal static JObject ResolveExpressions(JObject fields, object target)
        {
            var resolved = new JObject();
            if (fields == null) return resolved;
            if (target == null)
            {
                // No target to read current values from — just clone.
                foreach (var p in fields.Properties()) resolved[p.Name] = p.Value;
                return resolved;
            }

            var targetType = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var field = targetType.GetField(prop.Name, FieldFlags);
                if (field == null || !IsNumericType(field.FieldType))
                {
                    resolved[prop.Name] = prop.Value;
                    continue;
                }
                if (prop.Value.Type != JTokenType.String)
                {
                    resolved[prop.Name] = prop.Value;
                    continue;
                }

                var raw = prop.Value.Value<string>()?.Trim() ?? "";
                if (raw.Length < 2)
                {
                    resolved[prop.Name] = prop.Value;
                    continue;
                }

                char op = raw[0];
                if (op != '+' && op != '-' && op != '*' && op != '/' && op != '=')
                {
                    resolved[prop.Name] = prop.Value;
                    continue;
                }

                if (!double.TryParse(raw.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
                {
                    // Looked like an expression but the operand wasn't a number — pass through.
                    resolved[prop.Name] = prop.Value;
                    continue;
                }

                double current = 0;
                try { current = Convert.ToDouble(field.GetValue(target), CultureInfo.InvariantCulture); }
                catch { /* leave at 0 */ }

                double result = op switch
                {
                    '+' => current + operand,
                    '-' => current - operand,
                    '*' => current * operand,
                    '/' => operand == 0 ? current : current / operand,
                    '=' => operand,
                    _ => current,
                };

                resolved[prop.Name] = JToken.FromObject(result);
            }

            return resolved;
        }

        private static bool IsNumericType(Type t)
        {
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte)
                || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }
    }
}
