# Spark Tools MCP

MCP tool surface for the **Spark Game Creation Framework** (Unity asset by Blink). Adds `spark_*` tools to the Unity MCP server so Claude can author and batch-edit Spark database entries directly — no more chaining `manage_scriptable_object` calls by hand.

## Requirements

- Unity **6.3** or newer (matches Spark's requirement)
- **Spark Game Creation Framework** installed in the project (`Assets/Blink/Spark/`)
- **MCP for Unity** (`com.coplaydev.unity-mcp`) installed — this package piggybacks on Unity MCP's existing infrastructure

## Install

This package is meant to live as an **embedded package** during development (under `Packages/com.rockrabbit.spark-tools-mcp/` in the host Unity project). Once stable, push to a git remote and switch to a UPM git URL in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rockrabbit.spark-tools-mcp": "https://github.com/rockrabbit/spark-tools-mcp.git#v0.2.0"
  }
}
```

## Smoke-test

After installing, refresh Unity, wait for compilation to finish, then from a Claude Code session in the host project run:

```
spark_ping
```

Expected response shape:

```json
{
  "status": "success",
  "data": {
    "spark_tools_mcp_version": "0.2.0",
    "spark_entry_count": 366,
    "plugin_manifest_count": 14,
    "plugin_manifests": [
      { "plugin_name": "Items", "unique_id": "...", "version": "1.0.0", "category_count": 2, "tab_count": 6 },
      ...
    ],
    "unity_version": "6000.3.xfx"
  }
}
```

If `spark_ping` doesn't appear in the MCP tool list, see the Troubleshooting section below.

## Tool surface (v1, in progress)

| Phase | Tool | Status |
|---|---|---|
| 0 | `spark_ping` | Shipping in 0.1.0 (this release) |
| 1 | `spark_create_entry` | Planned |
| 2 | `spark_list_entry_types`, `spark_schema`, `spark_get_entry` | Planned |
| 3 | `spark_duplicate_entry`, `spark_batch_create_from_template`, `spark_generate_variations` | Planned |
| 4 | `spark_list_entries`, `spark_find_references`, `spark_validate_database` | Planned |
| 5 | `spark_update_entry`, `spark_batch_update`, `spark_delete_entry` | Planned |

All tools are auto-registered with Unity MCP's Python FastMCP server via the `[McpForUnityTool(AutoRegister = true)]` attribute. The whole group can be toggled on/off through `manage_tools` (group: `spark`).

Each parameterized tool declares its input schema via a nested `public class Parameters` whose `[ToolParameter]`-decorated properties Unity MCP's `ToolDiscoveryService` reflects into the JSON schema. The property name is used **verbatim** as the schema key, so those properties are deliberately `snake_case` to match the keys each handler reads from `@params`.

## Changelog

### 0.2.0

- **Fix: parameterized tools now emit their real input schemas.** Previously every tool — including those that require arguments like `spark_get_entry` and `spark_update_entry` — was registered with an empty `properties` object and `additionalProperties: false`, making them uncallable from MCP clients that validate input (any argument failed `InputValidationError`, and omitting arguments left the tool unable to know which entry to act on). Unity MCP's `ToolDiscoveryService` builds each tool's schema by reflecting over a nested `Parameters` class; the Spark handlers never declared one, so they fell back to a zero-property schema. Added a `Parameters` class to all twelve parameterized handlers. Genuinely parameterless tools (`spark_ping`, `spark_list_entry_types`, `spark_list_extension_types`, `spark_validate_database`) were already correct and are unchanged.

### 0.1.0

- Initial release.

## Troubleshooting

**`spark_ping` not in the tool list.**
1. Confirm `MCPForUnity.Editor.asmdef` is loaded — open the Project window's `Packages > MCP for Unity` and verify there are no compile errors.
2. Check Unity console for the line `Auto-discovered N tools and M resources` from `CommandRegistry.Initialize()`. If `N` includes the spark tools, they're registered on the Unity side but the Python bridge may need restarting.
3. Restart the MCP bridge: `Window > MCP for Unity > Restart Server`.

**Spark types unresolved (compile errors).**
- Verify Spark is installed at `Assets/Blink/Spark/`.
- This package's asmdef references `Spark.Core` and `Spark.Core.Editor`. If Spark's asmdef names have changed in a newer Spark release, update `Editor/Spark.Tools.MCP.Editor.asmdef` to match.

**Unity MCP version mismatch.**
- This package was built against `com.coplaydev.unity-mcp` 9.7+. If a future Unity MCP release renames `McpForUnityToolAttribute` or moves `CommandRegistry`, handlers will silently stop registering. Watch the Unity console at startup for `Auto-discovered N tools` — N should match the total handler count from this package plus Unity MCP's own.

## Architecture

The package is a thin C# Editor extension. Handlers are decorated with `[McpForUnityTool]` and are auto-discovered by Unity MCP's `CommandRegistry`; they call into Spark's existing authoring API (`DatabaseTabAssetOperations`, `PluginManifest`, `SparkDatabaseRegistry`) rather than reimplementing asset I/O. No separate Python or HTTP bridge is shipped.

See the in-repo design plan (in the host project's `~/.claude/plans/` directory if you're the Claude session that built this) or the `spark-framework` Claude Code skill for the broader architectural context.
