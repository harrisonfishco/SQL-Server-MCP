using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class QueryTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    [McpServerTool, Description(
        "Execute an arbitrary T-SQL statement and return results as JSON. " +
        "SELECT statements return an array of row objects. " +
        "DML/DDL statements return [{\"rowsAffected\": N}].")]
    public async Task<string> execute_query(
        [Description("The T-SQL statement to execute.")]
        string sql,
        [Description("Optional database to run the query against. " +
                     "Can also be specified in the connection string via Initial Catalog.")]
        string? databaseName = null,
        [Description("Query timeout in seconds. Defaults to 60.")]
        int timeoutSeconds = 60,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);
            var rows = await queryExecutor.ExecuteAsync(conn, sql, timeoutSeconds: timeoutSeconds);
            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Return the estimated XML execution plan for a query without executing it. " +
        "Uses SET SHOWPLAN_XML ON. The query is compiled but not run — no data is returned, " +
        "no locks are taken. Use this to analyze query plans safely on production.")]
    public async Task<string> get_estimated_plan(
        [Description("The T-SQL statement to generate a plan for.")]
        string sql,
        [Description("Optional database context for the query.")]
        string? databaseName = null,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            await using (var setOn = conn.CreateCommand())
            {
                setOn.CommandText = "SET SHOWPLAN_XML ON;";
                await setOn.ExecuteNonQueryAsync();
            }

            string? planXml;
            try
            {
                // With SHOWPLAN_XML ON the next statement returns its XML plan
                // as a single-row, single-column result set instead of executing.
                planXml = await queryExecutor.ExecuteScalarAsync(conn, sql);
            }
            finally
            {
                await using var setOff = conn.CreateCommand();
                setOff.CommandText = "SET SHOWPLAN_XML OFF;";
                await setOff.ExecuteNonQueryAsync();
            }

            if (planXml is null)
                return ToolResult.Fail("No execution plan was returned. Verify the SQL is a valid DML or SELECT statement.").ToJson();

            return ToolResult.Ok(new { plan = planXml }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Execute a query and return both the results and the actual runtime XML execution plan. " +
        "Uses SET STATISTICS XML ON. The query is fully executed — use get_estimated_plan " +
        "if you want a safe read-only plan analysis. " +
        "The plan reflects true row counts and operator costs from the actual run.")]
    public async Task<string> get_actual_plan(
        [Description("The T-SQL statement to execute and capture the plan for.")]
        string sql,
        [Description("Optional database context for the query.")]
        string? databaseName = null,
        [Description("Query timeout in seconds. Defaults to 60.")]
        int timeoutSeconds = 60,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            await using (var setOn = conn.CreateCommand())
            {
                setOn.CommandText = "SET STATISTICS XML ON;";
                await setOn.ExecuteNonQueryAsync();
            }

            List<Dictionary<string, object?>> rows;
            string? planXml;
            try
            {
                // STATISTICS XML ON causes SQL Server to append a second result set
                // after the normal query results containing the actual XML plan.
                (rows, planXml) = await queryExecutor.ExecuteWithPlanAsync(
                    conn, sql, timeoutSeconds: timeoutSeconds);
            }
            finally
            {
                await using var setOff = conn.CreateCommand();
                setOff.CommandText = "SET STATISTICS XML OFF;";
                await setOff.ExecuteNonQueryAsync();
            }

            return ToolResult.Ok(new
            {
                rows = queryExecutor.Serialize(rows),
                plan = planXml
            }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    private static string FormatException(Exception ex) => ex switch
    {
        SqlException sql => $"SQL error {sql.Number} (severity {sql.Class}, state {sql.State}): {sql.Message}",
        InvalidOperationException ioe => ioe.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}
