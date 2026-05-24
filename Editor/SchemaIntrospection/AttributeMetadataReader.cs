/*
 * Plugin: Spark Tools MCP
 *
 * Per-field metadata extractor. Reads Spark's custom inspector attributes and
 * the relevant UnityEngine ones, and condenses them into a JObject the schema
 * builder includes alongside each field entry.
 *
 * Attribute sources read:
 *   - Section            (Spark)  → section title + collapsible flag
 *   - DisplayNameTooltip (Spark)  → display_name + tooltip
 *   - ReadOnly           (Spark)  → writable: false
 *   - NestedData         (Spark)  → nested_data: {nested_id, storage_field}
 *   - NestedDataList     (Spark)  → nested_data_list: {nested_id_prefix, storage_field}
 *   - ScriptableObjectDropdown    (Spark) → selector: {kind, select_text, show_icons}
 *   - SparkDatabaseEntrySelector  (Spark) → selector: {kind, select_text, include_derived}
 *   - ConditionalField   (Spark)  → conditional: {field, expected_value}
 *   - TooltipAttribute   (Unity)  → tooltip (fallback if no DisplayNameTooltip)
 *   - Range              (Unity)  → range: {min, max}
 *   - Min                (Unity)  → range: {min}
 *   - TextArea           (Unity)  → text_area: {min_lines, max_lines}
 *   - HideInInspector    (Unity)  → hide_in_inspector: true (caller usually skips)
 *
 * Unrecognized attributes are ignored. This is intentionally not a "validate
 * the field against this metadata" tool — that's a runtime-time concern. Here
 * we just describe the field shape so the model can author it correctly.
 */

using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RockRabbit.SparkToolsMCP.SchemaIntrospection
{
    internal static class AttributeMetadataReader
    {
        /// <summary>
        /// Read all interesting attributes off the given FieldInfo and pack them into
        /// a JObject. Returns an empty object if nothing useful was found — never null.
        /// </summary>
        internal static JObject Read(FieldInfo field)
        {
            var meta = new JObject();
            if (field == null) return meta;

            foreach (var attr in field.GetCustomAttributes(inherit: true))
            {
                ExtractOne(attr, meta);
            }

            return meta;
        }

        private static void ExtractOne(object attr, JObject meta)
        {
            switch (attr)
            {
                // -- Spark inspector attributes --

                case SectionAttribute section:
                    meta["section"] = section.Title;
                    meta["section_collapsible"] = section.Collapsible;
                    if (!string.IsNullOrEmpty(section.Icon)) meta["section_icon"] = section.Icon;
                    return;

                case DisplayNameTooltipAttribute dnt:
                    meta["display_name"] = dnt.DisplayName;
                    if (!string.IsNullOrEmpty(dnt.Tooltip)) meta["tooltip"] = dnt.Tooltip;
                    return;

                case ReadOnlyAttribute readOnly:
                    meta["writable"] = false;
                    if (!string.IsNullOrEmpty(readOnly.tooltip) && meta["tooltip"] == null)
                    {
                        meta["tooltip"] = readOnly.tooltip;
                    }
                    return;

                case NestedDataAttribute nested:
                    meta["nested_data"] = new JObject
                    {
                        ["nested_id"] = nested.NestedId,
                        ["storage_field"] = nested.StorageFieldName,
                    };
                    return;

                case NestedDataListAttribute nestedList:
                    meta["nested_data_list"] = new JObject
                    {
                        ["nested_id_prefix"] = nestedList.NestedIdPrefix,
                        ["storage_field"] = nestedList.StorageFieldName,
                    };
                    return;

                case ScriptableObjectDropdownAttribute soDropdown:
                    meta["selector"] = new JObject
                    {
                        ["kind"] = "scriptable_object",
                        ["select_text"] = soDropdown.SelectText,
                        ["show_icons"] = soDropdown.ShowIcons,
                    };
                    return;

                case SparkDatabaseEntrySelectorAttribute dbSelector:
                    meta["selector"] = new JObject
                    {
                        ["kind"] = "spark_database_entry",
                        ["select_text"] = dbSelector.SelectText,
                        ["include_derived_types"] = dbSelector.IncludeDerivedTypes,
                    };
                    return;

                case ConditionalFieldAttribute cond:
                    meta["conditional"] = new JObject
                    {
                        ["field"] = cond.ConditionFieldName,
                        ["expected_value"] = cond.ExpectedValue != null ? JToken.FromObject(cond.ExpectedValue) : JValue.CreateNull(),
                    };
                    return;

                // -- UnityEngine attributes we care about --

                case TooltipAttribute tooltip:
                    if (meta["tooltip"] == null && !string.IsNullOrEmpty(tooltip.tooltip))
                    {
                        meta["tooltip"] = tooltip.tooltip;
                    }
                    return;

                case RangeAttribute range:
                    meta["range"] = new JObject
                    {
                        ["min"] = range.min,
                        ["max"] = range.max,
                    };
                    return;

                case MinAttribute min:
                    var rangeObj = meta["range"] as JObject ?? new JObject();
                    rangeObj["min"] = min.min;
                    meta["range"] = rangeObj;
                    return;

                case TextAreaAttribute textArea:
                    meta["text_area"] = new JObject
                    {
                        ["min_lines"] = textArea.minLines,
                        ["max_lines"] = textArea.maxLines,
                    };
                    return;

                case HideInInspector _:
                    meta["hide_in_inspector"] = true;
                    return;

                case HeaderAttribute header:
                    // Header is a sectioning hint older than Section. Capture for completeness.
                    if (meta["section"] == null && !string.IsNullOrEmpty(header.header))
                    {
                        meta["section"] = header.header;
                    }
                    return;

                case SerializeReference _:
                    meta["serialize_reference"] = true;
                    return;

                // Anything else — ignore. We could expand the list as we encounter new attrs.
            }
        }
    }
}
