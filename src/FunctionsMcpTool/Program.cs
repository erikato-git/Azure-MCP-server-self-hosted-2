using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static FunctionsMcpTool.ToolsInformation;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights()
    .AddSingleton(_ => new BlobServiceClient(
        Environment.GetEnvironmentVariable("AzureWebJobsStorage")));

// Demonstrate how you can define tool properties in Program.cs
// without requiring McpToolProperty input binding attributes:
builder
    .ConfigureMcpTool(EchoToolName)
    .WithProperty(EchoMessagePropertyName, McpToolPropertyType.String, EchoMessagePropertyDescription, required: true);

builder
    .ConfigureMcpTool(AzureEventsReportName)
    .WithProperty(AiSubscriptionNamesPropertyName, McpToolPropertyType.String, AiSubscriptionNamesPropertyDescription, required: false)
    .WithProperty(AiResourceGroupPropertyName, McpToolPropertyType.String, AiResourceGroupPropertyDescription, required: false)
    .WithProperty(AiTimeRangePropertyName, McpToolPropertyType.String, AiTimeRangePropertyDescription, required: false)
    .WithProperty(AiSeverityPropertyName, McpToolPropertyType.String, AiSeverityPropertyDescription, required: false)
    .WithProperty(AiEventIdPropertyName, McpToolPropertyType.String, AiEventIdPropertyDescription, required: false)
    .WithProperty(AiOperationIdPropertyName, McpToolPropertyType.String, AiOperationIdPropertyDescription, required: false)
    .WithProperty(AiStackFrameLimitPropertyName, McpToolPropertyType.String, AiStackFrameLimitPropertyDescription, required: false);

builder.Build().Run();
