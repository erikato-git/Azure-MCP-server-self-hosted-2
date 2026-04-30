# AzureEventsReportsTool — Specifications

## Introduction

`ApplicationInsightsTool` implements the MCP tool `azure_events_reports`. It discovers Application Insights resources across one or more Azure subscriptions at runtime, then either produces an aggregated health summary (event counts, request statistics, top exceptions, slowest requests) grouped by severity level with trend comparison against the preceding identical period, or drills down into a specific event or operation chain when an event or operation identifier is supplied. All output is structured markdown so an AI agent can present it directly in chat.

---

## Specifications

### SPEC-01 — Authentication: OBO flow

**Decision:** In production the tool acquires Azure tokens through the On-Behalf-Of (OBO) flow: the user's bearer token is extracted from App Service Authentication headers and exchanged for downstream ARM and Log Analytics tokens via a managed-identity federated credential. In local development a `ChainedTokenCredential` tries Azure CLI, VS Code, Visual Studio, and Azure Developer CLI in order.

**Why:** OBO propagates the end-user's identity to downstream Azure APIs so that RBAC is enforced on behalf of the caller rather than the function's own identity.

**Implementing code:** `BuildCredential`, `BuildOnBehalfOfCredential`, `GetUserToken`, `GetTenantId`, `BuildClientAssertionCallback`; the `ChainedTokenCredential` line in the dev branch.

---

### SPEC-02 — Subscription discovery: dynamic via ARM

**Decision:** Subscriptions are discovered dynamically at runtime by calling `ArmClient.GetSubscriptions().GetAllAsync()`. An optional comma-separated `subscription_names` parameter filters results by display name (case-insensitive). When the filter is absent, all accessible subscriptions are queried.

**Why:** Hard-coding subscription IDs would require redeployment every time the customer adds or renames a subscription; dynamic discovery keeps the tool maintenance-free.

**Implementing code:** `DiscoverAppInsightsResourcesAsync` — `GetAllAsync()` call and `nameFilter` guard condition; `subscription_names` parameter read in `Run`.

---

### SPEC-03 — Event types: all five tables

**Decision:** Every query that counts or searches events covers all five Application Insights telemetry tables: `exceptions`, `traces`, `requests`, `dependencies`, and `customEvents`.

**Why:** Limiting to a subset would hide categories of failures; unioning all five gives a complete picture of an application's health in a single query pass.

**Implementing code:** The `union` at the start of `trendQuery` in `GenerateResourceSummaryAsync`; the `union` in `DrillDownByEventIdAsync`; the `union` in `GetOperationChainAsync`.

---

### SPEC-04 — Default time window: 24 hours

**Decision:** When no `time_range` argument is provided, the tool analyses the last 24 hours. Any value may be overridden by supplying a number followed by a unit letter: `h` (hours), `d` (days), or `m` (minutes).

**Why:** 24 hours is the most common operational look-back window and matches default retention granularity in most Application Insights alerts.

**Implementing code:** `ParseTimeRange` method signature; `?? "24h"` default on the `timeRange` variable in `Run`.

---

### SPEC-05 — Intelligent routing: summary vs drill-down

**Decision:** A single tool entry point handles both modes. When neither `event_id` nor `operation_id` is present the tool generates an aggregated summary. When either identifier is present the tool automatically switches to drill-down mode without requiring an explicit `mode` parameter.

**Why:** A single parameter-driven entry point reduces cognitive load for the AI agent calling the tool and avoids an extra round-trip to select a mode.

**Implementing code:** `if (!string.IsNullOrEmpty(eventId) || !string.IsNullOrEmpty(operationId))` routing block in `Run`; `if (!string.IsNullOrEmpty(eventId))` priority check in `DrillDownAsync`.

---

### SPEC-06 — Output format: AI-agent-friendly markdown

**Decision:** All output is structured markdown: level-1/2/3/4 headers, bold labels, pipe tables, and fenced code blocks. Raw JSON is never returned as the top-level payload.

**Why:** An AI agent like Claude Code can render markdown natively in chat. Structured output removes the need for the agent to parse or reformat data before presenting it to the user.

**Implementing code:** `sb.AppendLine("# Application Insights Event Report")` as the root header in `GenerateSummaryAsync`; table and section construction throughout `GenerateResourceSummaryAsync` and `GetOperationChainAsync`.

---

### SPEC-07 — Tool name: `azure_events_reports`

**Decision:** The MCP tool is registered under the name `azure_events_reports`, following the project-wide snake_case naming convention for MCP tool identifiers.

**Why:** Consistency with other tools in the project and compatibility with MCP clients that expect snake_case names.

**Implementing code:** `AzureEventsReportName` constant in `ToolsInformation.cs`; `[Function(nameof(ApplicationInsightsTool))]` attribute on `Run`.

---

### SPEC-08 — Trend comparison: preceding identical period

**Decision:** Every event count in the summary is compared against the immediately preceding window of equal length. A single KQL query covers both periods using `iff(timestamp > ago(dur), "current", "previous")`. The `TrendIndicator` helper converts the percentage change into directional arrows.

**Why:** Comparing against an equal preceding window makes the trend meaningful regardless of time-of-day or day-of-week patterns.

**Implementing code:** `kqlPrev = ToKqlDuration(duration * 2)` in `GenerateResourceSummaryAsync`; `trendQuery` `union` comment line; `TrendIndicator` method signature; `ParseTrendData`.

---

### SPEC-09 — Operation chain window: ±30 minutes

**Decision:** When drilling into a specific operation, all related events are retrieved within a ±30-minute window centred on the pivot event's timestamp. This window is applied regardless of the user-supplied `time_range`.

**Why:** Distributed traces are typically short-lived; a tight ±30-minute window avoids returning unrelated operations while still capturing slow dependency chains.

**Implementing code:** `timeFilter` string construction inside `GetOperationChainAsync` — `todatetime(...) - 30m .. todatetime(...) + 30m`.

---

### SPEC-10 — Drill-down detail: stack trace + operation chain

**Decision:** A drill-down on an event returns: event metadata (type, timestamp, severity, operation ID), the full exception message, a stack trace (truncated at the configured frame limit per SPEC-19), custom dimensions, and the complete operation chain table.

**Why:** Providing the stack trace and the full operation chain in a single response avoids additional round-trips and gives the AI agent everything needed to diagnose an issue.

**Implementing code:** `FormatStackTrace(details, stackFrameLimit)` call in `DrillDownByEventIdAsync`; `GetOperationChainAsync` invocation that follows; `union` in `GetOperationChainAsync` which collects details for all event types.

---

### SPEC-11 — Time range format: single string with unit

**Decision:** Time range is expressed as a single string combining a numeric value and a unit letter: `"24h"`, `"7d"`, `"2h"`, `"30m"`. A separate `hours`/`days` pair of parameters was rejected in favour of brevity.

**Why:** A single compact string is faster to type in chat and reduces the number of optional parameters a user or agent needs to remember.

**Implementing code:** `time_range` parameter constant in `ToolsInformation.cs`; `ParseTimeRange` method signature; the `unit switch` expression inside `ParseTimeRange`.

---

### SPEC-12 — Event grouping: by severity level

**Decision:** Events are grouped by Application Insights severity level: Critical (4), Error (3), Warning (2), Information (1), Verbose (0). Grouping by time bucket or application name was considered but rejected to highlight operational health at a glance.

**Why:** Severity-first ordering surfaces the most actionable events immediately without requiring the user to scan a time series.

**Implementing code:** `SeverityLabel` method signature (maps integer level to string); KQL `summarize count = count() by period, eventType, severityLevel` in `trendQuery`; `ParseTrendData` which keys data by `(eventType, severityLabel)`.

---

### SPEC-13 — Empty resource groups: skip and mention

**Decision:** Resource groups that contain no Application Insights components are silently skipped during querying, but their names are collected and printed at the end of the relevant subscription section so the user is aware they were visited.

**Why:** Silently skipping reduces noise (no empty tables), while still reporting the groups prevents the user from wondering whether those resource groups were scanned at all.

**Implementing code:** `if (resources.Count == countBefore)` check in `DiscoverAppInsightsResourcesAsync` which adds to `emptyGroups`; `if (emptyInSub.Count > 0)` block in `GenerateSummaryAsync` which prints the note.

---

### SPEC-14 — Ambiguous drill-down: return disambiguation list

**Decision:** If an `event_id` or `operation_id` matches events in more than one App Insights resource, the tool returns a disambiguation list (resource group, resource name, event type, timestamp) and asks the user to narrow the scope via the `resource_group` parameter.

**Why:** Silently picking the first match could return the wrong event; failing entirely is unhelpful. A disambiguation list gives the user actionable information.

**Implementing code:** `if (matches.Count > 1)` branches in both `DrillDownByEventIdAsync` and `DrillDownByOperationIdAsync`.

---

### SPEC-15 — OBO scopes: ARM + Log Analytics

**Decision:** The single `TokenCredential` built by `BuildCredential` is passed to both `ArmClient` (which requests `https://management.azure.com/` tokens) and `LogsQueryClient` (which requests `https://api.loganalytics.io/` tokens). Each SDK client requests the appropriate scope automatically; no manual scope management is required in tool code.

**Why:** A single credential object simplifies the code and ensures consistent OBO token exchange for both downstream APIs.

**Implementing code:** `new ArmClient(credential)` and `new LogsQueryClient(credential)` in `Run`.

---

### SPEC-16 — Drill-down identifiers: itemId and operation_Id

**Decision:** Two identifiers are supported: `event_id` maps to the Application Insights `itemId` (unique per event), and `operation_id` maps to `operation_Id` (groups all events in a distributed trace). Both are optional. When `event_id` is supplied it takes priority over `operation_id`.

**Why:** `itemId` is suitable for pinpointing a single exception; `operation_Id` is suitable for tracing a complete request end-to-end. Supporting both covers the common diagnostics workflow.

**Implementing code:** `eventId` and `operationId` `GetArg` calls in `Run`; `if (!string.IsNullOrEmpty(eventId))` priority branch in `DrillDownAsync`.

---

### SPEC-17 — Language: English

**Decision:** All tool and property descriptions, as well as all generated report text, are in English. Tool names follow snake_case as required by MCP.

**Why:** English is the lingua franca for developer tooling and the working language of this codebase. snake_case aligns with the MCP specification.

**Implementing code:** This is a cross-cutting design choice applying to all string constants in `ToolsInformation.cs` and all `sb.AppendLine(...)` output strings. No single annotation point; SPEC-17 is therefore not annotated in source.

---

### SPEC-18 — Summary detail: four sections per resource

**Decision:** Each App Insights resource section in the summary report contains exactly four sub-sections: (1) event counts by severity with trend arrows, (2) request statistics (total, failed, error rate, P95, average duration), (3) top-5 exception types by count, (4) top-5 slowest requests by P95 duration.

**Why:** Four parallel KQL queries fired concurrently cover the most actionable dimensions of operational health without making the output so long that an AI agent loses context.

**Implementing code:** `trendTask`, `requestsTask`, `topExTask`, and `slowestTask` — the four `SafeQueryAsync` calls in `GenerateResourceSummaryAsync` that run concurrently via `Task.WhenAll`.

---

### SPEC-19 — Stack frame limit: configurable, default 15

**Decision:** The exception stack trace is truncated at N frames. N defaults to 15 and can be overridden per call via the `stack_frame_limit` parameter. A "truncated at N frames" note is appended when the limit is reached.

**Why:** Uncapped stack traces can be hundreds of lines long, consuming excessive context window. A default of 15 frames covers most root-cause scenarios while keeping output concise.

**Implementing code:** `DefaultStackFrameLimit = 15` constant; `if (frameCount >= frameLimit) break` inner loop guard; `if (frameCount >= frameLimit)` truncation note appended after the loop; all in `FormatStackTrace`.

---

### SPEC-20 — Filters: resource group, subscriptions, severity

**Decision:** Three optional parameters narrow the query scope: `resource_group` limits discovery to a single resource group; `subscription_names` limits to a comma-separated list of named subscriptions; `severity` restricts event counts and top-exception queries to the specified levels (Critical, Error, Warning, Information, Verbose, comma-separated).

**Why:** Without filters, a tenant with many subscriptions and resource groups could produce an enormous report or exceed query time limits. Filters make the tool practical for large environments.

**Implementing code:** `resource_group`, `subscription_names`, and `severity` parameters in `Run`; `severityFilter` variable (output of `ParseSeverityFilter`); `BuildSeverityWhereClause` method.

---

## Code Annotation Guide

Every specification listed above has one or more corresponding `// [SPEC-XX]` inline comments in `src/FunctionsMcpTool/ApplicationInsightsTool.cs`. The comment appears on the first significant line of each implementing code section — either a method signature, a variable declaration, or a control-flow statement. Where a spec is implemented inside a KQL string literal (where a `//` comment would be invalid), the annotation is placed on the C# comment line immediately above the string assignment.

SPEC-17 (Language: English) is a cross-cutting design choice with no single implementing line; it is intentionally not annotated in source.

To navigate from a spec number to the code: search the source file for `[SPEC-XX]` (e.g. `[SPEC-08]`) to jump directly to the relevant lines.
