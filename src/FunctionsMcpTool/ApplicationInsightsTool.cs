using Azure.Monitor.Query;
using Azure.ResourceManager;
using FunctionsMcpTool.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using static FunctionsMcpTool.ToolsInformation;

namespace FunctionsMcpTool;

public class ApplicationInsightsTool(
    ILogger<ApplicationInsightsTool> logger,
    CredentialBuilder credentialBuilder,
    ResourceDiscoveryService discoveryService,
    OutputFormatter outputFormatter,
    ReportBuilder reportBuilder)
{
    private const int DefaultStackFrameLimit = 15; // [SPEC-19]

    [Function(nameof(ApplicationInsightsTool))] // [SPEC-07]
    public async Task<string> Run(
        [McpToolTrigger(AzureEventsReportName, AzureEventsReportDescription)] ToolInvocationContext context)
    {
        logger.LogInformation("azure_events_reports invoked.");

        var credential = credentialBuilder.Build(context);
        var subscriptionNames = GetArg(context, AiSubscriptionNamesPropertyName);
        var resourceGroupFilter = GetArg(context, AiResourceGroupPropertyName);
        var timeRange = GetArg(context, AiTimeRangePropertyName) ?? "24h"; // [SPEC-04]
        var severity = GetArg(context, AiSeverityPropertyName);
        var eventId = GetArg(context, AiEventIdPropertyName); // [SPEC-16]
        var operationId = GetArg(context, AiOperationIdPropertyName); // [SPEC-16]
        var stackFrameLimit = int.TryParse(GetArg(context, AiStackFrameLimitPropertyName), out var sfl) ? sfl : DefaultStackFrameLimit;

        var duration = outputFormatter.ParseTimeRange(timeRange);
        var severityFilter = outputFormatter.ParseSeverityFilter(severity);

        var armClient = new ArmClient(credential); // [SPEC-15]
        var logsClient = new LogsQueryClient(credentialBuilder.BuildForLogs()); // [SPEC-15]

        var discovery = await discoveryService.DiscoverAsync(armClient, subscriptionNames, resourceGroupFilter);

        if (discovery.Resources.Count == 0 && discovery.EmptyGroups.Count == 0)
            return outputFormatter.BuildNoResourcesMessage(subscriptionNames, resourceGroupFilter);

        if (!string.IsNullOrEmpty(eventId) || !string.IsNullOrEmpty(operationId)) // [SPEC-05]
        {
            if (discovery.Resources.Count == 0)
                return outputFormatter.BuildNoResourcesMessage(subscriptionNames, resourceGroupFilter);
            return await reportBuilder.BuildDrillDownAsync(logsClient, discovery.Resources, eventId, operationId, duration, stackFrameLimit);
        }

        return await reportBuilder.BuildSummaryAsync(logsClient, discovery, duration, severityFilter, timeRange);
    }

    private static string? GetArg(ToolInvocationContext context, string key)
        => context.Arguments?.GetValueOrDefault(key)?.ToString();
}
