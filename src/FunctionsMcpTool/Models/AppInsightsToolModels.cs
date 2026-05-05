using Azure.Core;

namespace FunctionsMcpTool.Models;

internal record AiDiscoveryResult(
    List<AppInsightsResource> Resources,
    List<(string Subscription, string ResourceGroup)> EmptyGroups);

internal record AppInsightsResource(
    string SubscriptionName,
    string ResourceGroupName,
    string Name,
    ResourceIdentifier ResourceId);
