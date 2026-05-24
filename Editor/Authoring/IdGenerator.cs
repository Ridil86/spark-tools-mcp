/*
 * Plugin: Spark Tools MCP
 *
 * Generates entry IDs in the exact format Spark itself uses, so MCP-authored
 * entries are indistinguishable from Spark-Editor-authored ones.
 *
 * Reference: Assets/Blink/Spark/Core/Editor/Scripts/Tabs/Helpers/DatabaseTabAssetOperations.cs
 *   GenerateUniqueId (line 338): $"{Guid.NewGuid().ToString(\"N\")[..8]}_{DateTime.Now:yyyyMMddHHmmss}"
 *
 * We mirror this format exactly. The `pluginId` and `assetTypeName` parameters
 * on Spark's method are unused there too — we keep them out of our surface entirely.
 */

using System;

namespace RockRabbit.SparkToolsMCP.Authoring
{
    internal static class IdGenerator
    {
        internal static string New()
        {
            var guid = Guid.NewGuid().ToString("N")[..8];
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            return $"{guid}_{timestamp}";
        }
    }
}
