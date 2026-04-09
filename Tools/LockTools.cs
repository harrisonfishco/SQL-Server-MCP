using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class LockTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    [McpServerTool, Description(
        "List all current database locks with resource type, lock mode, request status, " +
        "and the owning session's login, host, and current wait info. " +
        "Optionally filter to a single database. Internal locks (session_id <= 0) are excluded.")]
    public async Task<string> get_locks(
        [Description("Filter to locks held in a specific database. Omit to return locks across all databases.")]
        string? databaseName = null,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                SELECT
                    tl.resource_type,
                    tl.resource_subtype,
                    DB_NAME(tl.resource_database_id)    AS database_name,
                    tl.resource_description,
                    tl.resource_associated_entity_id,
                    CASE tl.resource_type
                        WHEN 'OBJECT' THEN OBJECT_NAME(
                            tl.resource_associated_entity_id,
                            tl.resource_database_id)
                        ELSE NULL
                    END                                 AS object_name,
                    tl.request_mode,
                    tl.request_type,
                    tl.request_status,
                    tl.request_session_id,
                    tl.request_owner_type,
                    tl.request_owner_id,
                    s.login_name,
                    s.host_name,
                    s.program_name,
                    s.status                            AS session_status,
                    s.open_transaction_count,
                    r.command                           AS request_command,
                    r.wait_type,
                    r.wait_time                         AS wait_ms,
                    r.blocking_session_id
                FROM sys.dm_tran_locks tl
                LEFT JOIN sys.dm_exec_sessions s
                    ON s.session_id = tl.request_session_id
                LEFT JOIN sys.dm_exec_requests r
                    ON r.session_id = tl.request_session_id
                WHERE tl.request_session_id > 0
                  AND (@DatabaseName IS NULL
                       OR DB_NAME(tl.resource_database_id) = @DatabaseName)
                ORDER BY tl.request_session_id, tl.resource_type, tl.request_mode;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@DatabaseName"] = databaseName });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Show the full blocking chain across all sessions: which sessions are blocked, " +
        "by whom, and how deep the chain goes. Root blockers appear at depth 0. " +
        "Each row includes a chain_path (e.g. '52 -> 67 -> 91'), wait type, wait time, " +
        "and the SQL being run by each session. Returns zero rows if no blocking is occurring.")]
    public async Task<string> get_blocking_chains(
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                WITH BlockingChain AS (
                    -- Root blockers: blocking others but not blocked themselves
                    SELECT
                        r.session_id,
                        r.blocking_session_id,
                        r.wait_type,
                        r.wait_time,
                        r.status,
                        r.command,
                        r.sql_handle,
                        r.plan_handle,
                        0                                   AS depth,
                        CAST(r.session_id AS VARCHAR(1000)) AS chain_path
                    FROM sys.dm_exec_requests r
                    WHERE r.blocking_session_id = 0
                      AND EXISTS (
                          SELECT 1 FROM sys.dm_exec_requests r2
                          WHERE r2.blocking_session_id = r.session_id)

                    UNION ALL

                    -- Blocked sessions, walked recursively
                    SELECT
                        r.session_id,
                        r.blocking_session_id,
                        r.wait_type,
                        r.wait_time,
                        r.status,
                        r.command,
                        r.sql_handle,
                        r.plan_handle,
                        bc.depth + 1,
                        CAST(bc.chain_path + ' -> '
                             + CAST(r.session_id AS VARCHAR(10)) AS VARCHAR(1000))
                    FROM sys.dm_exec_requests r
                    INNER JOIN BlockingChain bc
                        ON bc.session_id = r.blocking_session_id
                )
                SELECT
                    bc.session_id,
                    bc.blocking_session_id,
                    bc.depth,
                    bc.chain_path,
                    bc.wait_type,
                    bc.wait_time / 1000.0               AS wait_seconds,
                    bc.status,
                    bc.command,
                    s.login_name,
                    s.host_name,
                    s.program_name,
                    s.open_transaction_count,
                    DB_NAME(r_self.database_id)         AS database_name,
                    t.text                              AS sql_text,
                    SUBSTRING(
                        t.text,
                        (r_self.statement_start_offset / 2) + 1,
                        (CASE r_self.statement_end_offset
                            WHEN -1 THEN DATALENGTH(t.text)
                            ELSE r_self.statement_end_offset
                        END - r_self.statement_start_offset) / 2 + 1
                    )                                   AS current_statement,
                    qp.query_plan
                FROM BlockingChain bc
                JOIN sys.dm_exec_sessions s
                    ON s.session_id = bc.session_id
                LEFT JOIN sys.dm_exec_requests r_self
                    ON r_self.session_id = bc.session_id
                OUTER APPLY sys.dm_exec_sql_text(bc.sql_handle) t
                OUTER APPLY sys.dm_exec_query_plan(bc.plan_handle) qp
                ORDER BY bc.chain_path;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql);

            var result = new
            {
                blockingDetected = rows.Count > 0,
                chainCount = rows.Count,
                chains = rows
            };

            return ToolResult.Ok(result).ToJson();
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
