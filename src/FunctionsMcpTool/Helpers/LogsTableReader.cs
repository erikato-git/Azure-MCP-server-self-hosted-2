using Azure.Monitor.Query.Models;

namespace FunctionsMcpTool.Helpers;

public class LogsTableReader
{
    internal int ColIndex(LogsTable table, string name)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name == name) return i;
        return -1;
    }

    internal long GetLong(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? 0 : row.GetInt64(idx) ?? 0;
    }

    internal double GetDouble(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? 0 : row.GetDouble(idx) ?? 0;
    }

    internal int GetInt(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? -1 : row.GetInt32(idx) ?? -1;
    }

    internal string? GetString(LogsTableRow row, LogsTable table, string col)
    {
        var idx = ColIndex(table, col);
        return idx < 0 ? null : row.GetString(idx);
    }
}
