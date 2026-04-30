using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using static FunctionsMcpTool.ToolsInformation;

namespace FunctionsMcpTool;

public class ApplicationInsightsTool(ILogger<ApplicationInsightsTool> logger, IHostEnvironment hostEnvironment)
{
    private const int DefaultStackFrameLimit = 15; // [SPEC-19]

    [Function(nameof(ApplicationInsightsTool))] // [SPEC-07]
    public async Task<string> Run(
        [McpToolTrigger(AzureEventsReportName, AzureEventsReportDescription)] ToolInvocationContext context)
    {
        logger.LogInformation("azure_events_reports invoked.");

        var credential = BuildCredential(context);
        var subscriptionNames = GetArg(context, AiSubscriptionNamesPropertyName);
        var resourceGroupFilter = GetArg(context, AiResourceGroupPropertyName);
        var timeRange = GetArg(context, AiTimeRangePropertyName) ?? "24h"; // [SPEC-04]
        var severity = GetArg(context, AiSeverityPropertyName);
        var eventId = GetArg(context, AiEventIdPropertyName); // [SPEC-16]
        var operationId = GetArg(context, AiOperationIdPropertyName); // [SPEC-16]
        var stackFrameLimit = int.TryParse(GetArg(context, AiStackFrameLimitPropertyName), out var sfl) ? sfl : DefaultStackFrameLimit;

        var duration = ParseTimeRange(timeRange);
        var severityFilter = ParseSeverityFilter(severity); // [SPEC-20]

        var armClient = new ArmClient(credential); // [SPEC-15]
        var logsClient = new LogsQueryClient(credential); // [SPEC-15]

        logger.LogInformation("Discovering Application Insights resources...");
        var discovery = await DiscoverAppInsightsResourcesAsync(armClient, subscriptionNames, resourceGroupFilter);

        if (discovery.Resources.Count == 0 && discovery.EmptyGroups.Count == 0)
            return BuildNoResourcesMessage(subscriptionNames, resourceGroupFilter);

        if (!string.IsNullOrEmpty(eventId) || !string.IsNullOrEmpty(operationId)) // [SPEC-05]
        {
            if (discovery.Resources.Count == 0)
                return BuildNoResourcesMessage(subscriptionNames, resourceGroupFilter);
            return await DrillDownAsync(logsClient, discovery.Resources, eventId, operationId, duration, stackFrameLimit);
        }

        return await GenerateSummaryAsync(logsClient, discovery, duration, severityFilter, timeRange);
    }

    // -------------------------------------------------------------------------
    // Summary
    // -------------------------------------------------------------------------

    private async Task<string> GenerateSummaryAsync(
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

            var subResources = discovery.Resources.Where(r => r.SubscriptionName == subName);
            foreach (var rgGroup in subResources.GroupBy(r => r.ResourceGroupName))
            {
                sb.AppendLine($"### Resource Group: {rgGroup.Key}");
                sb.AppendLine();

                foreach (var resource in rgGroup)
                {
                    sb.AppendLine($"#### {resource.Name}");
                    sb.AppendLine();
                    try
                    {
                        sb.Append(await GenerateResourceSummaryAsync(logsClient, resource, duration, severityFilter, logger));
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

    private static async Task<string> GenerateResourceSummaryAsync(
        LogsQueryClient logsClient,
        AppInsightsResource resource,
        TimeSpan duration,
        int[]? severityFilter,
        ILogger logger)
    {
        var kql = ToKqlDuration(duration);
        var kqlPrev = ToKqlDuration(duration * 2); // [SPEC-08]
        var sevWhere = BuildSeverityWhereClause(severityFilter);

        // [SPEC-03] [SPEC-08] — union all five event tables; dual-period window enables trend comparison
        var trendQuery = $@"union
    (exceptions | extend eventType = ""exception""),
    (traces | extend eventType = ""trace""),
    (requests | extend eventType = ""request"", severityLevel = toint(iff(success == false, 3, 1))),
    (dependencies | extend eventType = ""dependency"", severityLevel = toint(iff(success == false, 3, 1))),
    (customEvents | extend eventType = ""customEvent"", severityLevel = toint(1))
{sevWhere}| where timestamp > ago({kqlPrev})
| extend period = iff(timestamp > ago({kql}), ""current"", ""previous"")
| summarize count = count() by period, eventType, severityLevel
| order by period asc, count desc";

        var requestsQuery = $@"requests
| where timestamp > ago({kql})
| summarize total = count(), failed = countif(success == false),
    p95_ms = round(percentile(duration, 95), 0), avg_ms = round(avg(duration), 0)";

        var topExQuery = $@"exceptions
{sevWhere}| where timestamp > ago({kql})
| summarize count = count() by type
| order by count desc
| take 5";

        var slowestQuery = $@"requests
| where timestamp > ago({kql})
| summarize p95_ms = round(percentile(duration, 95), 0) by name
| order by p95_ms desc
| take 5";

        var trendTask = SafeQueryAsync(logsClient, resource.ResourceId, trendQuery, logger); // [SPEC-18]
        var requestsTask = SafeQueryAsync(logsClient, resource.ResourceId, requestsQuery, logger); // [SPEC-18]
        var topExTask = SafeQueryAsync(logsClient, resource.ResourceId, topExQuery, logger); // [SPEC-18]
        var slowestTask = SafeQueryAsync(logsClient, resource.ResourceId, slowestQuery, logger); // [SPEC-18]

        await Task.WhenAll(trendTask, requestsTask, topExTask, slowestTask);

        var sb = new StringBuilder();

        // Event counts by severity with trend
        var trendData = ParseTrendData(trendTask.Result?.Table);

        sb.AppendLine("**Events by severity — current vs previous period**");
        sb.AppendLine();
        sb.AppendLine("| Event Type | Severity | Current | Previous | Trend |");
        sb.AppendLine("|------------|----------|--------:|--------:|-------|");

        if (trendData.Count > 0)
        {
            foreach (var (evType, sev, current, previous) in trendData.OrderBy(x => x.EventType).ThenByDescending(x => x.Current))
                sb.AppendLine($"| {Cell(evType)} | {Cell(sev)} | {current:N0} | {previous:N0} | {TrendIndicator(current, previous)} |");
        }
        else
        {
            sb.AppendLine("| — | — | 0 | — | — |");
        }
        sb.AppendLine();

        // Request statistics
        var reqTable = requestsTask.Result?.Table;
        if (reqTable?.Rows.Count > 0)
        {
            var row = reqTable.Rows[0];
            var total = GetLong(row, reqTable, "total");
            var failed = GetLong(row, reqTable, "failed");
            var p95 = GetDouble(row, reqTable, "p95_ms");
            var avg = GetDouble(row, reqTable, "avg_ms");
            var errRate = total > 0 ? (double)failed / total * 100 : 0;

            sb.AppendLine("**Request statistics**");
            sb.AppendLine();
            sb.AppendLine($"- Requests: {total:N0} total, {failed:N0} failed ({errRate:F1}% error rate)");
            sb.AppendLine($"- Duration: P95 = {p95:N0} ms, avg = {avg:N0} ms");
            sb.AppendLine();
        }

        // Top exceptions
        var topExTable = topExTask.Result?.Table;
        if (topExTable?.Rows.Count > 0)
        {
            sb.AppendLine("**Top exception types**");
            sb.AppendLine();
            sb.AppendLine("| Exception | Count |");
            sb.AppendLine("|-----------|------:|");
            foreach (var row in topExTable.Rows)
            {
                var type = Truncate(Cell(GetString(row, topExTable, "type") ?? "(unknown)"), 70);
                var count = GetLong(row, topExTable, "count");
                sb.AppendLine($"| {type} | {count:N0} |");
            }
            sb.AppendLine();
        }

        // Slowest requests
        var slowTable = slowestTask.Result?.Table;
        if (slowTable?.Rows.Count > 0)
        {
            sb.AppendLine("**Slowest requests (P95)**");
            sb.AppendLine();
            sb.AppendLine("| Request | P95 (ms) |");
            sb.AppendLine("|---------|--------:|");
            foreach (var row in slowTable.Rows)
            {
                var name = Truncate(Cell(GetString(row, slowTable, "name") ?? "(unknown)"), 70);
                var p95 = GetDouble(row, slowTable, "p95_ms");
                sb.AppendLine($"| {name} | {p95:N0} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Drill-down
    // -------------------------------------------------------------------------

    private async Task<string> DrillDownAsync(
        LogsQueryClient logsClient,
        List<AppInsightsResource> resources,
        string? eventId,
        string? operationId,
        TimeSpan duration,
        int stackFrameLimit)
    {
        if (!string.IsNullOrEmpty(eventId)) // [SPEC-16] [SPEC-05]
            return await DrillDownByEventIdAsync(logsClient, resources, SanitizeId(eventId), stackFrameLimit);

        return await DrillDownByOperationIdAsync(logsClient, resources, SanitizeId(operationId!), duration, stackFrameLimit);
    }

    private async Task<string> DrillDownByEventIdAsync(
        LogsQueryClient logsClient,
        List<AppInsightsResource> resources,
        string eventId,
        int stackFrameLimit)
    {
        // [SPEC-03] — union all five event tables for event-ID lookup
        var query = $@"union
    (exceptions | extend eventType = ""exception"", details = tostring(details), customDims = tostring(customDimensions)),
    (requests    | extend eventType = ""request"",   details = """",              customDims = tostring(customDimensions)),
    (traces      | extend eventType = ""trace"",     details = """",              customDims = tostring(customDimensions)),
    (customEvents| extend eventType = ""customEvent"",details = """",             customDims = tostring(customDimensions)),
    (dependencies| extend eventType = ""dependency"",details = """",              customDims = tostring(customDimensions))
| where itemId == ""{eventId}""
| project eventType, timestamp, operation_Id, severityLevel, message, name, type, details, customDims, itemId
| take 1";

        var tasks = resources.Select(async resource =>
        {
            var result = await SafeQueryAsync(logsClient, resource.ResourceId, query, logger);
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
                sb.AppendLine($"- **{res.ResourceGroupName} / {res.Name}** — {GetString(row, table, "eventType")} at {GetString(row, table, "timestamp")}");
            return sb.ToString();
        }

        var (matchedResource, matchedRow, matchedTable) = matches[0];
        var operationId = GetString(matchedRow, matchedTable, "operation_Id");

        var sb2 = new StringBuilder();
        sb2.AppendLine("# Event Details");
        sb2.AppendLine($"**Resource:** {matchedResource.ResourceGroupName} / {matchedResource.Name}");
        sb2.AppendLine($"**Event ID:** `{eventId}`");
        sb2.AppendLine($"**Type:** {GetString(matchedRow, matchedTable, "eventType")}");
        sb2.AppendLine($"**Timestamp:** {GetString(matchedRow, matchedTable, "timestamp")}");
        sb2.AppendLine($"**Severity:** {SeverityLabel(GetInt(matchedRow, matchedTable, "severityLevel"))}");
        sb2.AppendLine($"**Operation ID:** `{operationId}`");
        sb2.AppendLine();

        var message = GetString(matchedRow, matchedTable, "message") ?? GetString(matchedRow, matchedTable, "name");
        if (!string.IsNullOrEmpty(message))
        {
            sb2.AppendLine("**Message:**");
            sb2.AppendLine(message);
            sb2.AppendLine();
        }

        var details = GetString(matchedRow, matchedTable, "details");
        if (!string.IsNullOrEmpty(details) && details != "[]")
        {
            sb2.AppendLine($"**Stack trace (first {stackFrameLimit} frames):**");
            sb2.AppendLine("```");
            sb2.AppendLine(FormatStackTrace(details, stackFrameLimit)); // [SPEC-10] [SPEC-19]
            sb2.AppendLine("```");
            sb2.AppendLine();
        }

        var customDims = GetString(matchedRow, matchedTable, "customDims");
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
            var pivotTs = GetString(matchedRow, matchedTable, "timestamp");
            sb2.Append(await GetOperationChainAsync(logsClient, matchedResource, operationId, pivotTs, stackFrameLimit, logger));
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
        var kql = ToKqlDuration(duration);
        var checkQuery = $@"union exceptions, requests, traces, customEvents, dependencies
| where operation_Id == ""{operationId}""
| where timestamp > ago({kql})
| take 1";

        var tasks = resources.Select(async resource =>
        {
            var result = await SafeQueryAsync(logsClient, resource.ResourceId, checkQuery, logger);
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

        return await GetOperationChainAsync(logsClient, matches[0], operationId, null, stackFrameLimit, logger);
    }

    private static async Task<string> GetOperationChainAsync(
        LogsQueryClient logsClient,
        AppInsightsResource resource,
        string operationId,
        string? pivotTimestamp,
        int stackFrameLimit,
        ILogger logger)
    {
        var timeFilter = string.IsNullOrEmpty(pivotTimestamp) // [SPEC-09]
            ? ""
            : $"\n| where timestamp between((todatetime(\"{pivotTimestamp}\") - 30m) .. (todatetime(\"{pivotTimestamp}\") + 30m))";

        // [SPEC-03] [SPEC-10] — union all five tables to build the full operation chain with stack details
        var chainQuery = $@"union
    (exceptions  | extend eventType = ""exception"",  summary = iif(isnotempty(type), type, outerMessage), details = tostring(details)),
    (requests    | extend eventType = ""request"",    summary = name,                                      details = """"),
    (traces      | extend eventType = ""trace"",      summary = message,                                   details = """"),
    (customEvents| extend eventType = ""customEvent"",summary = name,                                      details = """"),
    (dependencies| extend eventType = ""dependency"", summary = strcat(type, "": "", name),                details = """")
| where operation_Id == ""{operationId}""{timeFilter}
| project timestamp, eventType, summary, severityLevel, details, itemId
| order by timestamp asc
| take 200";

        var result = await SafeQueryAsync(logsClient, resource.ResourceId, chainQuery, logger);

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
            var ts = GetString(row, result.Table, "timestamp");
            var evType = GetString(row, result.Table, "eventType");
            var sev = SeverityLabel(GetInt(row, result.Table, "severityLevel"));
            var summary = Truncate(Cell(GetString(row, result.Table, "summary") ?? ""), 80);
            var eid = GetString(row, result.Table, "itemId") ?? "";
            sb.AppendLine($"| {ts} | {Cell(evType)} | {sev} | {summary} | `{eid}` |");

            var details = GetString(row, result.Table, "details");
            if (!string.IsNullOrEmpty(details) && details != "[]" && evType == "exception")
            {
                var shortTrace = FormatStackTraceFirstLine(details);
                if (!string.IsNullOrEmpty(shortTrace))
                    sb.AppendLine($"  > {Cell(shortTrace)}");
            }
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Resource discovery
    // -------------------------------------------------------------------------

    private static async Task<AiDiscoveryResult> DiscoverAppInsightsResourcesAsync(
        ArmClient armClient,
        string? subscriptionNameFilter,
        string? resourceGroupFilter)
    {
        var resources = new List<AppInsightsResource>();
        var emptyGroups = new List<(string Subscription, string ResourceGroup)>();

        var nameFilter = subscriptionNameFilter?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .ToHashSet();

        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync()) // [SPEC-02]
        {
            if (nameFilter?.Count > 0 && // [SPEC-02]
                !nameFilter.Contains(subscription.Data.DisplayName.ToLowerInvariant()))
                continue;

            await foreach (var resourceGroup in subscription.GetResourceGroups().GetAllAsync())
            {
                if (resourceGroupFilter != null &&
                    !resourceGroup.Data.Name.Equals(resourceGroupFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var countBefore = resources.Count;
                await foreach (var component in resourceGroup.GetGenericResourcesAsync(
                    filter: "resourceType eq 'microsoft.insights/components'"))
                {
                    resources.Add(new AppInsightsResource(
                        subscription.Data.DisplayName,
                        resourceGroup.Data.Name,
                        component.Data.Name,
                        component.Id));
                }

                if (resources.Count == countBefore) // [SPEC-13]
                    emptyGroups.Add((subscription.Data.DisplayName, resourceGroup.Data.Name));
            }
        }

        return new AiDiscoveryResult(resources, emptyGroups);
    }

    // -------------------------------------------------------------------------
    // KQL helpers
    // -------------------------------------------------------------------------

    private static async Task<LogsQueryResult?> SafeQueryAsync(
        LogsQueryClient client,
        ResourceIdentifier resourceId,
        string query,
        ILogger logger)
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

    private static List<(string EventType, string Severity, long Current, long Previous)> ParseTrendData(
        LogsTable? table)
    {
        if (table == null) return [];

        var data = new Dictionary<(string, string), (long Current, long Previous)>();

        foreach (var row in table.Rows)
        {
            var period = GetString(row, table, "period") ?? "";
            var evType = GetString(row, table, "eventType") ?? "";
            var sev = SeverityLabel(GetInt(row, table, "severityLevel"));
            var count = GetLong(row, table, "count");
            var key = (evType, sev);

            data.TryGetValue(key, out var existing);
            data[key] = period == "current"
                ? (count, existing.Previous)
                : (existing.Current, count);
        }

        return [.. data.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Current, kv.Value.Previous))];
    }

    private static string TrendIndicator(long current, long previous) // [SPEC-08]
    {
        if (previous == 0) return current == 0 ? "→" : "↑ new";
        var pct = (double)(current - previous) / previous * 100;
        return pct switch
        {
            > 50 => $"↑↑ +{pct:F0}%",
            > 10 => $"↑ +{pct:F0}%",
            < -50 => $"↓↓ {pct:F0}%",
            < -10 => $"↓ {pct:F0}%",
            _ => $"→ {pct:+0;-0;0}%"
        };
    }

    private static string BuildSeverityWhereClause(int[]? levels) // [SPEC-20]
    {
        if (levels == null || levels.Length == 0) return "";
        return $"| where severityLevel in ({string.Join(", ", levels)})\n";
    }

    private static string FormatStackTrace(string detailsJson, int frameLimit)
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

    private static string FormatStackTraceFirstLine(string detailsJson)
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

    // -------------------------------------------------------------------------
    // Parsing & formatting utilities
    // -------------------------------------------------------------------------

    private static TimeSpan ParseTimeRange(string s) // [SPEC-04] [SPEC-11]
    {
        if (string.IsNullOrWhiteSpace(s)) return TimeSpan.FromHours(24);
        var unit = s[^1..].ToLowerInvariant();
        var valueStr = s[..^1];
        if (!double.TryParse(valueStr, out var num)) return TimeSpan.FromHours(24);
        return unit switch // [SPEC-11]
        {
            "d" => TimeSpan.FromDays(num),
            "m" => TimeSpan.FromMinutes(num),
            _ => TimeSpan.FromHours(num)
        };
    }

    private static string ToKqlDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1 && ts.TotalDays == Math.Floor(ts.TotalDays))
            return $"{(int)ts.TotalDays}d";
        return $"{(int)Math.Ceiling(ts.TotalHours)}h";
    }

    private static int[]? ParseSeverityFilter(string? severity)
    {
        if (string.IsNullOrEmpty(severity)) return null;
        var levels = severity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant() switch
            {
                "critical" => 4,
                "error" => 3,
                "warning" => 2,
                "information" or "info" => 1,
                "verbose" => 0,
                _ => -1
            })
            .Where(v => v >= 0)
            .Distinct()
            .ToArray();
        return levels.Length > 0 ? levels : null;
    }

    private static string SeverityLabel(int level) => level switch // [SPEC-12]
    {
        4 => "Critical",
        3 => "Error",
        2 => "Warning",
        1 => "Information",
        0 => "Verbose",
        _ => "—"
    };

    // Sanitises event/operation IDs before interpolating into KQL to prevent injection.
    private static string SanitizeId(string id)
    {
        if (id.Length > 500 || id.Any(c => c is '"' or '\'' or '|' or ';' or '\n' or '\r'))
            throw new ArgumentException(
                $"Invalid characters in ID '{id}'. IDs must not contain quotes, pipes, semicolons, or newlines.");
        return id;
    }

    // Escapes characters that break markdown table formatting.
    private static string Cell(string? s) =>
        (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "..." + s[^(max - 3)..];

    private static string BuildNoResourcesMessage(string? subscriptionNames, string? resourceGroup)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(subscriptionNames)) parts.Add($"subscription(s) '{subscriptionNames}'");
        if (!string.IsNullOrEmpty(resourceGroup)) parts.Add($"resource group '{resourceGroup}'");
        var filter = parts.Count > 0 ? $" matching {string.Join(" and ", parts)}" : "";
        return $"No Application Insights resources found{filter}.";
    }

    private static string? GetArg(ToolInvocationContext context, string key)
        => context.Arguments?.GetValueOrDefault(key)?.ToString();

    // -------------------------------------------------------------------------
    // LogsTable column accessors
    // -------------------------------------------------------------------------

    private static int ColIndex(LogsTable table, string name)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name == name) return i;
        return -1;
    }

    private static long GetLong(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? 0 : row.GetInt64(idx) ?? 0;
    }

    private static double GetDouble(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? 0 : row.GetDouble(idx) ?? 0;
    }

    private static int GetInt(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? -1 : row.GetInt32(idx) ?? -1;
    }

    private static string? GetString(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? null : row.GetString(idx);
    }

    // -------------------------------------------------------------------------
    // OBO credential (same pattern as HelloToolWithAuth)
    // -------------------------------------------------------------------------

    private TokenCredential BuildCredential(ToolInvocationContext context) // [SPEC-01]
    {
        if (hostEnvironment.IsDevelopment())
        {
            return new ChainedTokenCredential( // [SPEC-01]
                new AzureCliCredential(),
                new VisualStudioCodeCredential(),
                new VisualStudioCredential(),
                new AzureDeveloperCliCredential());
        }

        return BuildOnBehalfOfCredential(context);
    }

    private static TokenCredential BuildOnBehalfOfCredential(ToolInvocationContext context)
    {
        if (!context.TryGetHttpTransport(out var transport))
            throw new InvalidOperationException("No HTTP transport available. Is App Service Authentication enabled?");

        var userToken = GetUserToken(transport!);
        var tenantId = GetTenantId(transport!);
        var assertion = BuildClientAssertionCallback();

        var clientId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID")
            ?? throw new InvalidOperationException("WEBSITE_AUTH_CLIENT_ID is not set.");

        return new OnBehalfOfCredential(tenantId, clientId, assertion, userToken);
    }

    private static string GetUserToken(HttpTransport transport)
    {
        if (transport.Headers.TryGetValue("X-MS-TOKEN-AAD-ACCESS-TOKEN", out var token) && !string.IsNullOrEmpty(token))
            return token;

        if (transport.Headers.TryGetValue("Authorization", out var header) && header.StartsWith("Bearer "))
            return header["Bearer ".Length..];

        throw new InvalidOperationException("No access token found. Is App Service Authentication enabled with token store?");
    }

    private static string GetTenantId(HttpTransport transport)
    {
        if (!transport.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded))
            throw new InvalidOperationException("X-MS-CLIENT-PRINCIPAL header is missing.");

        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        using var doc = System.Text.Json.JsonDocument.Parse(decoded);

        if (doc.RootElement.TryGetProperty("claims", out var claims) &&
            claims.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var claim in claims.EnumerateArray())
            {
                if (claim.TryGetProperty("typ", out var typ) &&
                    (typ.GetString() == "tid" ||
                     typ.GetString() == "http://schemas.microsoft.com/identity/claims/tenantid"))
                {
                    return claim.GetProperty("val").GetString()
                        ?? throw new InvalidOperationException("Tenant ID claim is null.");
                }
            }
        }

        throw new InvalidOperationException("Could not find tenant ID in X-MS-CLIENT-PRINCIPAL.");
    }

    private static Func<CancellationToken, Task<string>> BuildClientAssertionCallback()
    {
        var federatedMiClientId = Environment.GetEnvironmentVariable("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID")
            ?? throw new InvalidOperationException("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID is not set.");

        var audience = Environment.GetEnvironmentVariable("TokenExchangeAudience") ?? "api://AzureADTokenExchange";
        var mi = new ManagedIdentityCredential(federatedMiClientId);

        return async ct => (await mi.GetTokenAsync(
            new TokenRequestContext([$"{audience}/.default"]), ct).ConfigureAwait(false)).Token;
    }
}

internal record AiDiscoveryResult(
    List<AppInsightsResource> Resources,
    List<(string Subscription, string ResourceGroup)> EmptyGroups);

internal record AppInsightsResource(
    string SubscriptionName,
    string ResourceGroupName,
    string Name,
    ResourceIdentifier ResourceId);
