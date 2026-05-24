/*
 * Plugin: Spark Tools MCP
 *
 * Result envelope returned by every spark_* handler. The shape matches what
 * Unity MCP's Python bridge expects: a JObject with `status` and either `data`
 * or `error` populated.
 *
 * Use the static helpers (Success/Error) rather than constructing manually so
 * we get consistent field names across every tool.
 */

using Newtonsoft.Json.Linq;

namespace RockRabbit.SparkToolsMCP.Common
{
    internal static class McpResult
    {
        internal const string StatusSuccess = "success";
        internal const string StatusError = "error";

        internal static JObject Success(object data = null)
        {
            var result = new JObject
            {
                ["status"] = StatusSuccess,
            };
            if (data != null)
            {
                result["data"] = data is JToken token ? token : JToken.FromObject(data);
            }
            return result;
        }

        internal static JObject Error(string message, object details = null)
        {
            var result = new JObject
            {
                ["status"] = StatusError,
                ["error"] = message ?? "Unknown error",
            };
            if (details != null)
            {
                result["details"] = details is JToken token ? token : JToken.FromObject(details);
            }
            return result;
        }

        /// <summary>
        /// Partial success — some items in a batch succeeded, others failed.
        /// Used by batch handlers so a single failure doesn't lose the rest of the work.
        /// </summary>
        internal static JObject Partial(object successes, object failures)
        {
            return new JObject
            {
                ["status"] = StatusSuccess,
                ["partial"] = true,
                ["data"] = JToken.FromObject(new { successes, failures }),
            };
        }
    }
}
