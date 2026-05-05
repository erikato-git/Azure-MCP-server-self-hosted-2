namespace FunctionsMcpTool.Helpers;

internal static class KqlQueries
{
    // [SPEC-03] [SPEC-08] — union all five event tables; dual-period window enables trend comparison
    internal static string TrendQuery(string kql, string kqlPrev, string sevWhere) => $@"union
    (exceptions | extend eventType = ""exception""),
    (traces | extend eventType = ""trace""),
    (requests | extend eventType = ""request"", severityLevel = toint(iff(success == false, 3, 1))),
    (dependencies | extend eventType = ""dependency"", severityLevel = toint(iff(success == false, 3, 1))),
    (customEvents | extend eventType = ""customEvent"", severityLevel = toint(1))
{sevWhere}| where timestamp > ago({kqlPrev})
| extend period = iff(timestamp > ago({kql}), ""current"", ""previous"")
| summarize count = count() by period, eventType, severityLevel
| order by period asc, count desc";

    internal static string RequestsQuery(string kql) => $@"requests
| where timestamp > ago({kql})
| summarize total = count(), failed = countif(success == false),
    p95_ms = round(percentile(duration, 95), 0), avg_ms = round(avg(duration), 0)";

    internal static string TopExceptionsQuery(string kql, string sevWhere) => $@"exceptions
{sevWhere}| where timestamp > ago({kql})
| summarize count = count() by type
| order by count desc
| take 5";

    internal static string SlowestRequestsQuery(string kql) => $@"requests
| where timestamp > ago({kql})
| summarize p95_ms = round(percentile(duration, 95), 0) by name
| order by p95_ms desc
| take 5";

    // [SPEC-03] — union all five event tables for event-ID lookup
    internal static string EventByIdQuery(string eventId) => $@"union
    (exceptions | extend eventType = ""exception"", details = tostring(details), customDims = tostring(customDimensions)),
    (requests    | extend eventType = ""request"",   details = """",              customDims = tostring(customDimensions)),
    (traces      | extend eventType = ""trace"",     details = """",              customDims = tostring(customDimensions)),
    (customEvents| extend eventType = ""customEvent"",details = """",             customDims = tostring(customDimensions)),
    (dependencies| extend eventType = ""dependency"",details = """",              customDims = tostring(customDimensions))
| where itemId == ""{eventId}""
| project eventType, timestamp, operation_Id, severityLevel, message, name, type, details, customDims, itemId
| take 1";

    internal static string OperationExistsQuery(string operationId, string kql) => $@"union exceptions, requests, traces, customEvents, dependencies
| where operation_Id == ""{operationId}""
| where timestamp > ago({kql})
| take 1";

    // [SPEC-03] [SPEC-10] — union all five tables to build the full operation chain with stack details
    internal static string OperationChainQuery(string operationId, string timeFilter) => $@"union
    (exceptions  | extend eventType = ""exception"",  summary = iif(isnotempty(type), type, outerMessage), details = tostring(details)),
    (requests    | extend eventType = ""request"",    summary = name,                                      details = """"),
    (traces      | extend eventType = ""trace"",      summary = message,                                   details = """"),
    (customEvents| extend eventType = ""customEvent"",summary = name,                                      details = """"),
    (dependencies| extend eventType = ""dependency"", summary = strcat(type, "": "", name),                details = """")
| where operation_Id == ""{operationId}""{timeFilter}
| project timestamp, eventType, summary, severityLevel, details, itemId
| order by timestamp asc
| take 200";
}
