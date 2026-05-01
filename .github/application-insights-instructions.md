# Application Insights & Azure Monitor Instructions

This file contains pre-configured settings and context for efficiently querying Application Insights and Azure Monitor for the Azure MCP Server deployment.

## Deployment Context

**Do not re-discover these values** - they are stable across sessions and stored in `.azure/<environment-name>/.env`

### Subscription & Tenant
- **Subscription ID:** `${AZURE_SUBSCRIPTION_ID}` (from `.azure/Azure-MCP-server-self-hosted-2/.env`)
- **Tenant ID:** `${AZURE_TENANT_ID}` (from `.azure/Azure-MCP-server-self-hosted-2/.env`)
- **Resource Group:** `${AZURE_RESOURCE_GROUP}` (from `.azure/Azure-MCP-server-self-hosted-2/.env`)
- **Azure Location:** `${AZURE_LOCATION}` (from `.azure/Azure-MCP-server-self-hosted-2/.env`)

### Log Analytics Workspaces
Your subscription has 4 Log Analytics workspaces. **Use these for monitoring queries:**

| Workspace Name | Workspace ID | Use Case |
|---|---|---|
| `log-zhzfxhqk62ue2` | `a67bb9b1-bf47-4fe8-9fe3-f24c1a5d8963` | MCP Server logs (primary) |
| `log-qh26aeplmv43q` | `abe32c14-9b7b-4f1f-8d91-65b49d9d1fe8` | (secondary deployment) |
| `DefaultWorkspace-ee3decd2-e126-4d8e-b42c-4df305c8e6e8-WEU` | `504bd06e-c630-4c27-860c-09eaef05ff17` | Default workspace |
| `JiraGithubConversationEngine-law` | `d4269411-39cb-43e7-a4d6-d555340c1764` | (unrelated project) |

**Primary Workspace for this project:** `log-zhzfxhqk62ue2`

### Application Insights Configuration
- **App Insights Instrumentation Key:** `${APPLICATIONINSIGHTS_CONNECTION_STRING}` (from `.azure/Azure-MCP-server-self-hosted-2/.env`)
- **Application ID:** Found in Azure Portal > Application Insights resource
- **Ingestion Endpoint:** Region-specific (northeurope-2.in.applicationinsights.azure.com)
- **Live Diagnostics Endpoint:** Region-specific (northeurope.livediagnostics.monitor.azure.com)

## Function Apps Monitored
These 5 services send telemetry to Application Insights:

1. **func-tools-zhzfxhqk62ue2** (Tools service - primary MCP endpoint)
2. **func-weather-zhzfxhqk62ue2** (Weather service)
3. **func-prompts-zhzfxhqk62ue2** (Prompts service)
4. **func-resources-zhzfxhqk62ue2** (Resources service)
5. **func-apps-zhzfxhqk62ue2** (Apps service)

## Efficient Querying Patterns

### When Requesting Reports

**Instead of:**
```
"Give me a report for rg-Azure-MCP-server-self-hosted-2 over the last 24h"
```

**Use pre-configured parameters:**
```
- Subscription: ${AZURE_SUBSCRIPTION_ID}
- Tenant: ${AZURE_TENANT_ID} (always specify to avoid auth failures)
- Resource Group: ${AZURE_RESOURCE_GROUP}
- Log Analytics Workspace: log-zhzfxhqk62ue2
```

### Query Time Windows
Use these standard time ranges to speed up discovery:
- `24h` - Last 24 hours (default)
- `7d` - Last 7 days
- `30d` - Last 30 days
- ISO format: `2026-04-30T01:30:00Z` to `2026-05-01T01:30:00Z`

### Common Tables to Query
1. **AppServiceHTTPLogs** - HTTP request logs from Function Apps
2. **AzureActivity** - Control plane operations
3. **AppExceptions** - Application exceptions
4. **AppTraces** - Application traces/logging
5. **PerformanceCounters** - System performance data

### Valid Metrics for Function Apps
These are the available metrics - do NOT try to query others:
- `MemoryWorkingSet` - Current memory usage in bytes
- `AverageMemoryWorkingSet` - Average memory usage
- `CpuPercentage` - CPU usage percentage
- `InstanceCount` - Number of instances
- `OnDemandFunctionExecutionCount` - Function execution count
- `OnDemandFunctionExecutionUnits` - Execution units consumed
- `AlwaysReadyFunctionExecutionCount` - Always-ready function executions
- `AlwaysReadyFunctionExecutionUnits` - Always-ready execution units
- `AlwaysReadyUnits` - Always-ready unit minutes

**Metric Namespace:** `Microsoft.Web/sites`

## Cost Optimization Tips

1. **Always specify tenant ID** - Avoids auth loop retries (saves ~30 seconds per query)
2. **Use workspace queries** - Faster than metric queries for time-series data
3. **Set time windows upfront** - Prevents repeated discovery calls
4. **Batch queries** - Request multiple metrics in one call instead of separate calls
5. **Use predefined queries** - "recent" and "errors" are optimized KQL queries
6. **Avoid retry loops** - Always provide tenant + subscription explicitly

## Sample Efficient Query

```
Monitor Query:
- Workspace: log-zhzfxhqk62ue2
- Resource Group: ${AZURE_RESOURCE_GROUP}
- Subscription: ${AZURE_SUBSCRIPTION_ID}
- Tenant: ${AZURE_TENANT_ID}
- Table: AzureActivity
- Hours: 24
- Query: "recent" (predefined KQL)
```

This approach eliminates 3-4 API calls for workspace/workspace discovery and auth verification.
