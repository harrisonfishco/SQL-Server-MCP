using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class TransactionTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    [McpServerTool, Description(
        "List all open (uncommitted) transactions with age in seconds, owning session details, " +
        "database, current SQL, and wait/blocking info. " +
        "Sorted oldest-first so long-running transactions that risk log growth appear at the top. " +
        "Use minAgeSeconds to focus on transactions that have been open suspiciously long.")]
    public async Task<string> get_open_transactions(
        [Description("Only return transactions open for at least this many seconds. Defaults to 0 (all open transactions).")]
        int minAgeSeconds = 0,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                SELECT
                    at.transaction_id,
                    at.name                                         AS transaction_name,
                    at.transaction_begin_time,
                    DATEDIFF(SECOND, at.transaction_begin_time,
                             GETDATE())                             AS age_seconds,
                    CASE at.transaction_type
                        WHEN 1 THEN 'ReadWrite'
                        WHEN 2 THEN 'ReadOnly'
                        WHEN 3 THEN 'System'
                        WHEN 4 THEN 'Distributed'
                        ELSE CAST(at.transaction_type AS VARCHAR(10))
                    END                                             AS transaction_type,
                    CASE at.transaction_state
                        WHEN 0 THEN 'Uninitialized'
                        WHEN 1 THEN 'NotStarted'
                        WHEN 2 THEN 'Active'
                        WHEN 3 THEN 'Ended'
                        WHEN 4 THEN 'CommitStarted'
                        WHEN 5 THEN 'Prepared'
                        WHEN 6 THEN 'Committed'
                        WHEN 7 THEN 'RollingBack'
                        WHEN 8 THEN 'RolledBack'
                        ELSE CAST(at.transaction_state AS VARCHAR(10))
                    END                                             AS transaction_state,
                    st.session_id,
                    st.is_user_transaction,
                    st.open_transaction_count,
                    s.login_name,
                    s.host_name,
                    s.program_name,
                    s.status                                        AS session_status,
                    s.cpu_time,
                    s.memory_usage * 8                              AS memory_kb,
                    s.reads,
                    s.writes,
                    s.transaction_isolation_level,
                    CASE s.transaction_isolation_level
                        WHEN 0 THEN 'Unspecified'
                        WHEN 1 THEN 'ReadUncommitted'
                        WHEN 2 THEN 'ReadCommitted'
                        WHEN 3 THEN 'RepeatableRead'
                        WHEN 4 THEN 'Serializable'
                        WHEN 5 THEN 'Snapshot'
                        ELSE 'Unknown'
                    END                                             AS isolation_level_desc,
                    DB_NAME(dt.database_id)                         AS database_name,
                    dt.database_transaction_log_bytes_used          AS log_bytes_used,
                    dt.database_transaction_log_bytes_reserved      AS log_bytes_reserved,
                    r.command,
                    r.wait_type,
                    r.wait_time                                     AS wait_ms,
                    r.blocking_session_id,
                    r.percent_complete,
                    t.text                                          AS current_sql
                FROM sys.dm_tran_active_transactions at
                JOIN sys.dm_tran_session_transactions st
                    ON st.transaction_id = at.transaction_id
                JOIN sys.dm_exec_sessions s
                    ON s.session_id = st.session_id
                LEFT JOIN sys.dm_exec_requests r
                    ON r.session_id = st.session_id
                LEFT JOIN sys.dm_tran_database_transactions dt
                    ON dt.transaction_id = at.transaction_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE DATEDIFF(SECOND, at.transaction_begin_time, GETDATE()) >= @MinAgeSeconds
                ORDER BY at.transaction_begin_time ASC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@MinAgeSeconds"] = minAgeSeconds });

            var result = new
            {
                transactionCount = rows.Count,
                transactions = rows
            };

            return ToolResult.Ok(result).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "List all locks currently held by a specific transaction, identified by its transaction_id. " +
        "Use get_open_transactions first to find the transaction_id. " +
        "Shows resource type, database, object name, lock mode, and status for every lock the transaction owns.")]
    public async Task<string> get_transaction_locks(
        [Description("The transaction_id to inspect. Obtain this from get_open_transactions.")]
        long transactionId,
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
                    tl.request_session_id
                FROM sys.dm_tran_locks tl
                WHERE tl.request_owner_id = @TransactionId
                  AND tl.request_owner_type = 'TRANSACTION'
                ORDER BY tl.resource_type, tl.request_mode;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@TransactionId"] = transactionId });

            if (rows.Count == 0)
                return ToolResult.Fail(
                    $"No locks found for transaction_id {transactionId}. " +
                    "The transaction may have committed, rolled back, or the ID is incorrect.").ToJson();

            var result = new
            {
                transactionId,
                lockCount = rows.Count,
                locks = rows
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
