using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class SessionTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    [McpServerTool, Description(
        "List all currently executing requests with elapsed time, wait type, blocking session, " +
        "CPU, logical reads, full SQL text, the specific statement being run, and the live query plan XML. " +
        "Sorted by total elapsed time descending so the longest-running queries appear first.")]
    public async Task<string> get_running_queries(
        [Description("Only return requests that have been running for at least this many seconds. Defaults to 0 (all).")]
        int minElapsedSeconds = 0,
        [Description("Include system session requests (e.g. background tasks). Defaults to false.")]
        bool includeSystemSessions = false,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                SELECT
                    r.session_id,
                    r.request_id,
                    r.start_time,
                    DATEDIFF(MILLISECOND, r.start_time, GETDATE()) / 1000.0
                                                    AS elapsed_seconds,
                    r.status,
                    r.command,
                    DB_NAME(r.database_id)          AS database_name,
                    r.wait_type,
                    r.wait_time / 1000.0            AS wait_seconds,
                    r.last_wait_type,
                    r.wait_resource,
                    r.blocking_session_id,
                    r.open_transaction_count,
                    r.percent_complete,
                    r.estimated_completion_time,
                    r.cpu_time,
                    r.total_elapsed_time,
                    r.reads,
                    r.writes,
                    r.logical_reads,
                    r.granted_query_memory,
                    r.dop,
                    s.login_name,
                    s.host_name,
                    s.program_name,
                    s.is_user_process,
                    t.text                          AS sql_text,
                    SUBSTRING(
                        t.text,
                        (r.statement_start_offset / 2) + 1,
                        (CASE r.statement_end_offset
                            WHEN -1 THEN DATALENGTH(t.text)
                            ELSE r.statement_end_offset
                        END - r.statement_start_offset) / 2 + 1
                    )                               AS current_statement,
                    qp.query_plan
                FROM sys.dm_exec_requests r
                JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                OUTER APPLY sys.dm_exec_query_plan(r.plan_handle) qp
                WHERE (@IncludeSystem = 1 OR s.is_user_process = 1)
                  AND DATEDIFF(SECOND, r.start_time, GETDATE()) >= @MinElapsed
                ORDER BY r.total_elapsed_time DESC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@IncludeSystem"] = includeSystemSessions ? 1 : 0,
                    ["@MinElapsed"]    = minElapsedSeconds
                });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Get comprehensive detail for a single session: login, host, program, isolation level, " +
        "open transactions, CPU, memory, reads/writes, tempdb page usage, connection info, " +
        "and the SQL currently being executed.")]
    public async Task<string> get_session_detail(
        [Description("The session_id (SPID) to inspect.")]
        int sessionId,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                SELECT
                    s.session_id,
                    s.login_time,
                    s.host_name,
                    s.program_name,
                    s.login_name,
                    s.status,
                    s.cpu_time,
                    s.memory_usage * 8              AS memory_kb,
                    s.reads,
                    s.writes,
                    s.logical_reads,
                    s.transaction_isolation_level,
                    CASE s.transaction_isolation_level
                        WHEN 0 THEN 'Unspecified'
                        WHEN 1 THEN 'ReadUncommitted'
                        WHEN 2 THEN 'ReadCommitted'
                        WHEN 3 THEN 'RepeatableRead'
                        WHEN 4 THEN 'Serializable'
                        WHEN 5 THEN 'Snapshot'
                        ELSE 'Unknown'
                    END                             AS isolation_level_desc,
                    s.open_transaction_count,
                    s.row_count,
                    s.last_request_start_time,
                    s.last_request_end_time,
                    s.is_user_process,
                    r.status                        AS request_status,
                    r.command,
                    r.database_id,
                    DB_NAME(r.database_id)          AS database_name,
                    r.wait_type,
                    r.wait_time                     AS wait_ms,
                    r.blocking_session_id,
                    r.open_transaction_count        AS request_open_transactions,
                    r.percent_complete,
                    r.cpu_time                      AS request_cpu_ms,
                    r.total_elapsed_time            AS request_elapsed_ms,
                    r.reads                         AS request_reads,
                    r.writes                        AS request_writes,
                    r.logical_reads                 AS request_logical_reads,
                    r.granted_query_memory,
                    r.dop,
                    t.text                          AS current_sql,
                    c.net_transport,
                    c.client_net_address,
                    c.client_tcp_port,
                    c.connection_id,
                    c.num_reads                     AS connection_reads,
                    c.num_writes                    AS connection_writes,
                    c.connect_time,
                    tdb.internal_objects_alloc_page_count   AS tempdb_internal_pages,
                    tdb.user_objects_alloc_page_count       AS tempdb_user_pages,
                    tdb.internal_objects_dealloc_page_count AS tempdb_internal_pages_freed,
                    tdb.user_objects_dealloc_page_count     AS tempdb_user_pages_freed
                FROM sys.dm_exec_sessions s
                LEFT JOIN sys.dm_exec_requests r
                    ON r.session_id = s.session_id
                LEFT JOIN sys.dm_exec_connections c
                    ON c.session_id = s.session_id
                LEFT JOIN sys.dm_db_session_space_usage tdb
                    ON tdb.session_id = s.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE s.session_id = @SessionId;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@SessionId"] = sessionId });

            if (rows.Count == 0)
                return ToolResult.Fail($"Session {sessionId} not found or has already ended.").ToJson();

            // Return as a single object rather than a one-element array
            return ToolResult.Ok(rows[0]).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Kill a SQL Server session (SPID) or check its rollback progress. " +
        "WARNING: Killing a session rolls back all of its open transactions and terminates its connection. " +
        "Use withStatusOnly=true first to safely check rollback progress on a session already being killed.")]
    public async Task<string> kill_session(
        [Description("The session_id (SPID) to kill. Must be between 1 and 32767.")]
        int sessionId,
        [Description("If true, runs KILL ... WITH STATUSONLY to report rollback progress without killing. Defaults to false.")]
        bool withStatusOnly = false,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        // Validate before any interpolation — KILL does not support parameterized queries
        if (sessionId <= 0 || sessionId > 32767)
            return ToolResult.Fail($"Invalid session_id {sessionId}. Must be between 1 and 32767.").ToJson();

        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            // Confirm the session exists before issuing KILL
            const string checkSql = """
                SELECT session_id, login_name, host_name, program_name, status
                FROM sys.dm_exec_sessions
                WHERE session_id = @SessionId;
                """;

            var check = await queryExecutor.ExecuteAsync(conn, checkSql,
                new Dictionary<string, object?> { ["@SessionId"] = sessionId });

            if (check.Count == 0)
                return ToolResult.Fail($"Session {sessionId} not found or has already ended.").ToJson();

            // sessionId is range-validated above — safe to interpolate
            var killSql = withStatusOnly
                ? $"KILL {sessionId} WITH STATUSONLY;"
                : $"KILL {sessionId};";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = killSql;
            cmd.CommandTimeout = 30;

            if (withStatusOnly)
            {
                // WITH STATUSONLY returns a result set describing rollback progress
                var status = await queryExecutor.ExecuteAsync(conn, killSql);
                return ToolResult.Ok(new { sessionId, statusOnly = true, status }).ToJson();
            }

            await cmd.ExecuteNonQueryAsync();
            return ToolResult.Ok(new { sessionId, killed = true }).ToJson();
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
