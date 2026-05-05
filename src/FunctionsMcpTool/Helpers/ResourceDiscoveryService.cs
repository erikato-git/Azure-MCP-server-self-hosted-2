using Azure.ResourceManager;
using FunctionsMcpTool.Models;
using Microsoft.Extensions.Logging;

namespace FunctionsMcpTool.Helpers;

public class ResourceDiscoveryService(ILogger<ResourceDiscoveryService> logger)
{
    internal async Task<AiDiscoveryResult> DiscoverAsync(
        ArmClient armClient,
        string? subscriptionNameFilter,
        string? resourceGroupFilter)
    {
        logger.LogInformation("Discovering Application Insights resources...");

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
}
