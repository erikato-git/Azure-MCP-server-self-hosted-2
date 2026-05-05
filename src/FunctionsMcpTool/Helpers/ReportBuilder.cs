using Azure.Monitor.Query;
using FunctionsMcpTool.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace FunctionsMcpTool.Helpers;

public class ReportBuilder(
    ILogger<ReportBuilder> logger,
    KqlQueryService kqlQueryService,
    LogsTableReader tableReader,
    OutputFormatter formatter)
{
    internal async Task<string> BuildSummaryAsync(
        LogsQueryClient logsClient,
        AiDiscoveryResult discovery,
        TimeSpan duration,
        int[]? severityFilter,
        string timeRangeLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Application Insights Event Report"); // [SPEC-06]
        sb.AppendLine($"**Time range:** Last {timeRangeLabel} | **App Insights resources found:** {discovery.Resources.Count}");
        sb.AppendLine();

        var allSubscriptions = discovery.Resources.Select(r => r.SubscriptionName)
            .Concat(discovery.EmptyGroups.Select(g => g.Subscription))
            .Distinct()
            .OrderBy(s => s);

        foreach (var subName in allSubscriptions)
        {
            sb.AppendLine($"## Subscription: {subName}");
            sb.AppendLine();

            foreach (var rgGroup in discovery.Resources.Where(r => r.SubscriptionName == subName).GroupBy(r => r.ResourceGroupName))
            {
                sb.AppendLine($"### Resource Group: {rgGroup.Key}");
                sb.AppendLine();

                foreach (var resource in rgGroup)
                {
                    sb.AppendLine($"#### {resource.Name}");
                    sb.AppendLine();
                    try
                    {
                        sb.Append(await BuildResourceSummaryAsync(logsClient, resource, duration, severityFilter));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to query {Resource}", resource.Name);
                        sb.AppendLine($"> Could not query this resource: {ex.Message}");
                        sb.AppendLine();
                    }
                }
            }

            var emptyInSub = discovery.EmptyGroups
                .Where(g => g.Subscription == subName)
                .Select(g => g.ResourceGroup)
                .ToList();
            if (emptyInSub.Count > 0) // [SPEC-13]
            {
                sb.AppendLine($"> No Application Insights resources found in: {string.Join(", ", emptyInSub)}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> BuildResourceSummaryAsync(
        LogsQueryClient logsClient,
        AppInsightsResource resource,
        TimeSpan duration,
        int[]? severityFilter)
    {
        var kql = formatter.ToKqlDuration(duration);
        var kqlPrev = formatter.ToKqlDuration(duration * 2); // [SPEC-08]
        var sevWhere = formatter.BuildSeverityWhereClause(severityFilter);

        var trendTask = kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.TrendQuery(kql, kqlPrev, sevWhere));
        var requestsTask = kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.RequestsQuery(kql));
        var topExTask = kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.TopExceptionsQuery(kql, sevWhere));
        var slowestTask = kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.SlowestRequestsQuery(kql));

        await Task.WhenAll(trendTask, requestsTask, topExTask, slowestTask);

        var sb = new StringBuilder();

        var trendData = kqlQueryService.ParseTrendData(trendTask.Result?.Table);
        sb.AppendLine("**Events by severity — current vs previous period**");
        sb.AppendLine();
        sb.AppendLine("| Event Type | Severity | Current | Previous | Trend |");
        sb.AppendLine("|------------|----------|--------:|--------:|-------|");
        if (trendData.Count > 0)
        {
            foreach (var (evType, sev, current, previous) in trendData.OrderBy(x => x.EventType).ThenByDescending(x => x.Current))
                sb.AppendLine($"| {formatter.Cell(evType)} | {formatter.Cell(sev)} | {current:N0} | {previous:N0} | {formatter.TrendIndicator(current, previous)} |");
        }
        else
        {
            sb.AppendLine("| — | — | 0 | — | — |");
        }
        sb.AppendLine();

        var reqTable = requestsTask.Result?.Table;
        if (reqTable?.Rows.Count > 0)
        {
            var row = reqTable.Rows[0];
            var total = tableReader.GetLong(row, reqTable, "total");
            var failed = tableReader.GetLong(row, reqTable, "failed");
            var p95 = tableReader.GetDouble(row, reqTable, "p95_ms");
            var avg = tableReader.GetDouble(row, reqTable, "avg_ms");
            var errRate = total > 0 ? (double)failed / total * 100 : 0;

            sb.AppendLine("**Request statistics**");
            sb.AppendLine();
            sb.AppendLine($"- Requests: {total:N0} total, {failed:N0} failed ({errRate:F1}% error rate)");
            sb.AppendLine($"- Duration: P95 = {p95:N0} ms, avg = {avg:N0} ms");
            sb.AppendLine();
        }

        var topExTable = topExTask.Result?.Table;
        if (topExTable?.Rows.Count > 0)
        {
            sb.AppendLine("**Top exception types**");
            sb.AppendLine();
            sb.AppendLine("| Exception | Count |");
            sb.AppendLine("|-----------|------:|");
            foreach (var row in topExTable.Rows)
            {
                var type = formatter.Truncate(formatter.Cell(tableReader.GetString(row, topExTable, "type") ?? "(unknown)"), 70);
                var count = tableReader.GetLong(row, topExTable, "count");
                sb.AppendLine($"| {type} | {count:N0} |");
            }
            sb.AppendLine();
        }

        var slowTable = slowestTask.Result?.Table;
        if (slowTable?.Rows.Count > 0)
        {
            sb.AppendLine("**Slowest requests (P95)**");
            sb.AppendLine();
            sb.AppendLine("| Request | P95 (ms) |");
            sb.AppendLine("|---------|--------:|");
            foreach (var row in slowTable.Rows)
            {
                var name = formatter.Truncate(formatter.Cell(tableReader.GetString(row, slowTable, "name") ?? "(unknown)"), 70);
                var p95 = tableReader.GetDouble(row, slowTable, "p95_ms");
                sb.AppendLine($"| {name} | {p95:N0} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal async Task<string> BuildDrillDownAsync(
        LogsQueryClient logsClient,
        List<AppInsightsResource> resources,
        string? eventId,
        string? operationId,
        TimeSpan duration,
        int stackFrameLimit)
    {
        if (!string.IsNullOrEmpty(eventId)) // [SPEC-16] [SPEC-05]
            return await DrillDownByEventIdAsync(logsClient, resources, formatter.SanitizeId(eventId), stackFrameLimit);

        return await DrillDownByOperationIdAsync(logsClient, resources, formatter.SanitizeId(operationId!), duration, stackFrameLimit);
    }

    private async Task<string> DrillDownByEventIdAsync(
        LogsQueryClient logsClient,
        List<AppInsightsResource> resources,
        string eventId,
        int stackFrameLimit)
    {
        var tasks = resources.Select(async resource =>
        {
            var result = await kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.EventByIdQuery(eventId));
            return (resource, result);
        }).ToList();

        var allResults = await Task.WhenAll(tasks);
        var matches = allResults
            .Where(x => x.result?.Table?.Rows.Count > 0)
            .Select(x => (Resource: x.resource, Row: x.result!.Table!.Rows[0], Table: x.result!.Table!))
            .ToList();

        if (matches.Count == 0)
            return $"No event found with ID `{eventId}`.";

        if (matches.Count > 1) // [SPEC-14]
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Found event `{eventId}` in {matches.Count} resources. Specify `resource_group` to narrow down:");
            sb.AppendLine();
            foreach (var (res, row, table) in matches)
                sb.AppendLine($"- **{res.ResourceGroupName} / {res.Name}** — {tableReader.GetString(row, table, "eventType")} at {tableReader.GetString(row, table, "timestamp")}");
            return sb.ToString();
        }

        var (matchedResource, matchedRow, matchedTable) = matches[0];
        var operationId = tableReader.GetString(matchedRow, matchedTable, "operation_Id");

        var sb2 = new StringBuilder();
        sb2.AppendLine("# Event Details");
        sb2.AppendLine($"**Resource:** {matchedResource.ResourceGroupName} / {matchedResource.Name}");
        sb2.AppendLine($"**Event ID:** `{eventId}`");
        sb2.AppendLine($"**Type:** {tableReader.GetString(matchedRow, matchedTable, "eventType")}");
        sb2.AppendLine($"**Timestamp:** {tableReader.GetString(matchedRow, matchedTable, "timestamp")}");
        sb2.AppendLine($"**Severity:** {formatter.SeverityLabel(tableReader.GetInt(matchedRow, matchedTable, "severityLevel"))}");
        sb2.AppendLine($"**Operation ID:** `{operationId}`");
        sb2.AppendLine();

        var message = tableReader.GetString(matchedRow, matchedTable, "message") ?? tableReader.GetString(matchedRow, matchedTable, "name");
        if (!string.IsNullOrEmpty(message))
        {
            sb2.AppendLine("**Message:**");
            sb2.AppendLine(message);
            sb2.AppendLine();
        }

        var details = tableReader.GetString(matchedRow, matchedTable, "details");
        if (!string.IsNullOrEmpty(details) && details != "[]")
        {
            sb2.AppendLine($"**Stack trace (first {stackFrameLimit} frames):**");
            sb2.AppendLine("```");
            sb2.AppendLine(kqlQueryService.FormatStackTrace(details, stackFrameLimit));
            sb2.AppendLine("```");
            sb2.AppendLine();
        }

        var customDims = tableReader.GetString(matchedRow, matchedTable, "customDims");
        if (!string.IsNullOrEmpty(customDims) && customDims != "{}")
        {
            sb2.AppendLine("**Custom dimensions:**");
            sb2.AppendLine("```json");
            sb2.AppendLine(customDims);
            sb2.AppendLine("```");
            sb2.AppendLine();
        }

        if (!string.IsNullOrEmpty(operationId))
        {
            var pivotTs = tableReader.GetString(matchedRow, matchedTable, "timestamp");
            sb2.Append(await BuildOperationChainAsync(logsClient, matchedResource, operationId, pivotTs, stackFrameLimit));
        }

        return sb2.ToString();
    }

    private async Task<string> DrillDownByOperationIdAsync(
        LogsQueryClient logsClient,
        List<AppInsightsResource> resources,
        string operationId,
        TimeSpan duration,
        int stackFrameLimit)
    {
        var kql = formatter.ToKqlDuration(duration);

        var tasks = resources.Select(async resource =>
        {
            var result = await kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.OperationExistsQuery(operationId, kql));
            return (resource, hasData: result?.Table?.Rows.Count > 0);
        }).ToList();

        var allResults = await Task.WhenAll(tasks);
        var matches = allResults.Where(x => x.hasData).Select(x => x.resource).ToList();

        if (matches.Count == 0)
            return $"No events found for operation ID `{operationId}` in the last {kql}.";

        if (matches.Count > 1) // [SPEC-14]
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Operation `{operationId}` found in {matches.Count} resources. Specify `resource_group` to narrow down:");
            foreach (var r in matches)
                sb.AppendLine($"- {r.ResourceGroupName} / {r.Name}");
            return sb.ToString();
        }

        return await BuildOperationChainAsync(logsClient, matches[0], operationId, null, stackFrameLimit);
    }

    private async Task<string> BuildOperationChainAsync(
        LogsQueryClient logsClient,
        AppInsightsResource resource,
        string operationId,
        string? pivotTimestamp,
        int stackFrameLimit)
    {
        var timeFilter = string.IsNullOrEmpty(pivotTimestamp) // [SPEC-09]
            ? ""
            : $"\n| where timestamp between((todatetime(\"{pivotTimestamp}\") - 30m) .. (todatetime(\"{pivotTimestamp}\") + 30m))";

        var result = await kqlQueryService.SafeQueryAsync(logsClient, resource.ResourceId, KqlQueries.OperationChainQuery(operationId, timeFilter));

        if (result?.Table == null || result.Table.Rows.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine($"## Operation chain (ID: `{operationId}`)");
        sb.AppendLine($"**Resource:** {resource.ResourceGroupName} / {resource.Name} | **Events:** {result.Table.Rows.Count}");
        sb.AppendLine();
        sb.AppendLine("| Timestamp | Type | Severity | Summary | Event ID |");
        sb.AppendLine("|-----------|------|----------|---------|----------|");

        foreach (var row in result.Table.Rows)
        {
            var ts = tableReader.GetString(row, result.Table, "timestamp");
            var evType = tableReader.GetString(row, result.Table, "eventType");
            var sev = formatter.SeverityLabel(tableReader.GetInt(row, result.Table, "severityLevel"));
            var summary = formatter.Truncate(formatter.Cell(tableReader.GetString(row, result.Table, "summary") ?? ""), 80);
            var eid = tableReader.GetString(row, result.Table, "itemId") ?? "";
            sb.AppendLine($"| {ts} | {formatter.Cell(evType)} | {sev} | {summary} | `{eid}` |");

            var details = tableReader.GetString(row, result.Table, "details");
            if (!string.IsNullOrEmpty(details) && details != "[]" && evType == "exception")
            {
                var shortTrace = kqlQueryService.FormatStackTraceFirstLine(details);
                if (!string.IsNullOrEmpty(shortTrace))
                    sb.AppendLine($"  > {formatter.Cell(shortTrace)}");
            }
        }

        return sb.ToString();
    }
}
