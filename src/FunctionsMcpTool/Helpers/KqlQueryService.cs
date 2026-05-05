using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace FunctionsMcpTool.Helpers;

public class KqlQueryService(
    ILogger<KqlQueryService> logger,
    LogsTableReader tableReader,
    OutputFormatter formatter)
{
    internal async Task<LogsQueryResult?> SafeQueryAsync(
        LogsQueryClient client,
        ResourceIdentifier resourceId,
        string query) // [SPEC-18]
    {
        try
        {
            var response = await client.QueryResourceAsync(resourceId, query, QueryTimeRange.All);
            return response.Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KQL query failed for {ResourceId}", resourceId);
            return null;
        }
    }

    internal List<(string EventType, string Severity, long Current, long Previous)> ParseTrendData(
        LogsTable? table)
    {
        if (table == null) return [];

        var data = new Dictionary<(string, string), (long Current, long Previous)>();

        foreach (var row in table.Rows)
        {
            var period = tableReader.GetString(row, table, "period") ?? "";
            var evType = tableReader.GetString(row, table, "eventType") ?? "";
            var sev = formatter.SeverityLabel(tableReader.GetInt(row, table, "severityLevel"));
            var count = tableReader.GetLong(row, table, "count");
            var key = (evType, sev);

            data.TryGetValue(key, out var existing);
            data[key] = period == "current"
                ? (count, existing.Previous)
                : (existing.Current, count);
        }

        return [.. data.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Current, kv.Value.Previous))];
    }

    internal string FormatStackTrace(string detailsJson, int frameLimit) // [SPEC-10] [SPEC-19]
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            var sb = new StringBuilder();
            var frameCount = 0;

            foreach (var detail in doc.RootElement.EnumerateArray())
            {
                if (detail.TryGetProperty("message", out var msg))
                    sb.AppendLine(msg.GetString());

                if (detail.TryGetProperty("parsedStack", out var stack))
                {
                    foreach (var frame in stack.EnumerateArray())
                    {
                        if (frameCount >= frameLimit) break; // [SPEC-19]
                        if (frame.TryGetProperty("assembly", out var asm) &&
                            frame.TryGetProperty("method", out var method))
                        {
                            var line = frame.TryGetProperty("line", out var ln) ? $":{ln.GetInt32()}" : "";
                            sb.AppendLine($"  at {asm.GetString()}.{method.GetString()}{line}");
                            frameCount++;
                        }
                    }
                }
            }

            if (frameCount >= frameLimit) // [SPEC-19]
                sb.AppendLine($"  ... (truncated at {frameLimit} frames)");

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return detailsJson;
        }
    }

    internal string FormatStackTraceFirstLine(string detailsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            foreach (var detail in doc.RootElement.EnumerateArray())
                if (detail.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? "";
        }
        catch { }
        return "";
    }
}
