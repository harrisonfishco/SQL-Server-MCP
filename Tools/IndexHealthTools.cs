using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class IndexHealthTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    private static readonly HashSet<string> ValidSampleModes =
        new(StringComparer.OrdinalIgnoreCase) { "LIMITED", "SAMPLED", "DETAILED" };

    private static readonly HashSet<string> ValidOrderByColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "avg_cpu_time", "avg_duration", "execution_count", "avg_logical_io_reads"
        };

    [McpServerTool, Description(
        "Return the top missing indexes recommended by SQL Server's DMVs, ordered by estimated impact score. " +
        "Each row includes the equality/inequality/included columns and a ready-to-run CREATE INDEX statement. " +
        "Data accumulates since the last server restart and resets on restart or index changes.")]
    public async Task<string> get_missing_indexes(
        [Description("Filter to a specific database. Omit to return missing indexes across all databases.")]
        string? databaseName = null,
        [Description("Minimum impact score (avg_total_user_cost * avg_user_impact * seeks+scans). Defaults to 0.")]
        double minImpact = 0.0,
        [Description("Maximum number of results to return. Defaults to 50.")]
        int topN = 50,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        if (topN <= 0 || topN > 1000)
            return ToolResult.Fail("topN must be between 1 and 1000.").ToJson();

        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            // topN is range-validated above — safe to interpolate
            var sql = $"""
                SELECT TOP ({topN})
                    DB_NAME(mid.database_id)            AS database_name,
                    mid.statement                       AS full_table_name,
                    migs.avg_total_user_cost,
                    migs.avg_user_impact,
                    migs.user_seeks,
                    migs.user_scans,
                    migs.last_user_seek,
                    migs.last_user_scan,
                    CAST(
                        migs.avg_total_user_cost
                        * migs.avg_user_impact
                        * (migs.user_seeks + migs.user_scans)
                    AS DECIMAL(18, 2))                  AS impact_score,
                    mid.equality_columns,
                    mid.inequality_columns,
                    mid.included_columns,
                    'CREATE INDEX [IX_'
                        + REPLACE(REPLACE(REPLACE(mid.statement, '[', ''), ']', ''), '.', '_')
                        + '_' + ISNULL(REPLACE(mid.equality_columns, ', ', '_'), '')
                        + '] ON ' + mid.statement
                        + ' ('
                        + ISNULL(mid.equality_columns, '')
                        + CASE
                            WHEN mid.inequality_columns IS NOT NULL
                                 AND mid.equality_columns IS NOT NULL THEN ', '
                            WHEN mid.inequality_columns IS NOT NULL THEN ''
                            ELSE ''
                          END
                        + ISNULL(mid.inequality_columns, '')
                        + ')'
                        + CASE
                            WHEN mid.included_columns IS NOT NULL
                            THEN ' INCLUDE (' + mid.included_columns + ')'
                            ELSE ''
                          END
                        + ';'                           AS suggested_create_index
                FROM sys.dm_db_missing_index_details mid
                JOIN sys.dm_db_missing_index_groups mig
                    ON mid.index_handle = mig.index_handle
                JOIN sys.dm_db_missing_index_group_stats migs
                    ON mig.index_group_handle = migs.group_handle
                WHERE (@DatabaseName IS NULL
                       OR DB_NAME(mid.database_id) = @DatabaseName)
                  AND migs.avg_total_user_cost
                      * migs.avg_user_impact
                      * (migs.user_seeks + migs.user_scans) >= @MinImpact
                ORDER BY impact_score DESC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@DatabaseName"] = databaseName,
                    ["@MinImpact"]    = minImpact
                });

            return ToolResult.Ok(new { missingIndexCount = rows.Count, missingIndexes = rows }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Show how often each index is used for seeks, scans, lookups, and updates since the last server restart. " +
        "High update counts with zero reads indicate unused indexes that waste write overhead and should be considered for removal. " +
        "Sorted by user_updates descending to surface the costliest unused indexes first.")]
    public async Task<string> get_index_usage_stats(
        [Description("Name of the database to analyze.")]
        string databaseName,
        [Description("Filter to a specific table. Omit to return all tables.")]
        string? tableName = null,
        [Description("When true, only returns indexes with zero reads (seeks + scans + lookups = 0). Useful for finding removal candidates.")]
        bool includeUnusedOnly = false,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            const string sql = """
                SELECT
                    s.name                              AS schema_name,
                    o.name                              AS table_name,
                    i.name                              AS index_name,
                    i.type_desc                         AS index_type,
                    i.is_primary_key,
                    i.is_unique,
                    i.is_disabled,
                    ISNULL(ius.user_seeks, 0)           AS user_seeks,
                    ISNULL(ius.user_scans, 0)           AS user_scans,
                    ISNULL(ius.user_lookups, 0)         AS user_lookups,
                    ISNULL(ius.user_updates, 0)         AS user_updates,
                    ISNULL(ius.user_seeks, 0)
                        + ISNULL(ius.user_scans, 0)
                        + ISNULL(ius.user_lookups, 0)   AS total_reads,
                    ius.last_user_seek,
                    ius.last_user_scan,
                    ius.last_user_lookup,
                    ius.last_user_update,
                    ISNULL(ius.system_seeks, 0)         AS system_seeks,
                    ISNULL(ius.system_scans, 0)         AS system_scans
                FROM sys.indexes i
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                LEFT JOIN sys.dm_db_index_usage_stats ius
                    ON ius.object_id = i.object_id
                   AND ius.index_id  = i.index_id
                   AND ius.database_id = DB_ID()
                WHERE o.type = 'U'
                  AND i.type > 0
                  AND (@TableName IS NULL OR o.name = @TableName)
                  AND (@UnusedOnly = 0
                       OR (ISNULL(ius.user_seeks,   0) = 0
                           AND ISNULL(ius.user_scans,   0) = 0
                           AND ISNULL(ius.user_lookups, 0) = 0))
                ORDER BY ISNULL(ius.user_updates, 0) DESC, o.name, i.name;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@TableName"]  = tableName,
                    ["@UnusedOnly"] = includeUnusedOnly ? 1 : 0
                });

            return ToolResult.Ok(new { indexCount = rows.Count, indexes = rows }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Return fragmentation percentage for indexes in a database. " +
        "Each row includes a recommended_action: OK (< 5%), REORGANIZE (5–30%), or REBUILD (> 30%). " +
        "Scope to a specific table to keep runtime short — scanning all indexes in a large database can be slow. " +
        "Use sampleMode LIMITED (fastest) for routine checks, SAMPLED or DETAILED for accurate diagnosis.")]
    public async Task<string> get_index_fragmentation(
        [Description("Name of the database to analyze.")]
        string databaseName,
        [Description("Filter to a specific table. Strongly recommended for large databases.")]
        string? tableName = null,
        [Description("Minimum fragmentation percentage to return. Defaults to 5.0.")]
        double minFragmentationPct = 5.0,
        [Description("Scan mode passed to sys.dm_db_index_physical_stats. " +
                     "LIMITED (default, fastest), SAMPLED, or DETAILED (slowest, most accurate).")]
        string sampleMode = "LIMITED",
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        sampleMode = sampleMode.ToUpperInvariant();
        if (!ValidSampleModes.Contains(sampleMode))
            return ToolResult.Fail(
                $"Invalid sampleMode '{sampleMode}'. Must be one of: LIMITED, SAMPLED, DETAILED.").ToJson();

        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            // sampleMode is validated against an allowlist above — safe to interpolate.
            // sys.dm_db_index_physical_stats mode argument does accept a variable in some
            // versions but literal is the most compatible form across SQL Server editions.
            var sql = $"""
                SELECT
                    s.name                              AS schema_name,
                    o.name                              AS table_name,
                    i.name                              AS index_name,
                    i.type_desc                         AS index_type,
                    ips.partition_number,
                    ips.index_depth,
                    ips.avg_fragmentation_in_percent,
                    ips.fragment_count,
                    ips.avg_fragment_size_in_pages,
                    ips.page_count,
                    CASE
                        WHEN ips.avg_fragmentation_in_percent < 5  THEN 'OK'
                        WHEN ips.avg_fragmentation_in_percent < 30 THEN 'REORGANIZE'
                        ELSE 'REBUILD'
                    END                                 AS recommended_action
                FROM sys.dm_db_index_physical_stats(
                    DB_ID(),
                    OBJECT_ID(@TableName),
                    NULL,
                    NULL,
                    '{sampleMode}') ips
                JOIN sys.indexes i
                    ON i.object_id = ips.object_id AND i.index_id = ips.index_id
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type = 'U'
                  AND i.type > 0
                  AND ips.index_level = 0
                  AND ips.avg_fragmentation_in_percent >= @MinFragPct
                ORDER BY ips.avg_fragmentation_in_percent DESC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@TableName"]  = (object?)tableName ?? DBNull.Value,
                    ["@MinFragPct"] = minFragmentationPct
                },
                timeoutSeconds: 300);

            return ToolResult.Ok(new { sampleMode, indexCount = rows.Count, indexes = rows }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Return cumulative wait statistics since the last server restart, ordered by total wait time. " +
        "Benign idle waits (SLEEP_*, BROKER_*, XE_*, etc.) are excluded by default. " +
        "The pct_total column shows each wait type's share of all non-idle wait time — " +
        "the most actionable signal for diagnosing server-wide performance bottlenecks.")]
    public async Task<string> get_wait_statistics(
        [Description("Maximum number of wait types to return. Defaults to 30.")]
        int topN = 30,
        [Description("Exclude well-known benign idle wait types to reduce noise. Defaults to true.")]
        bool excludeIdle = true,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        if (topN <= 0 || topN > 500)
            return ToolResult.Fail("topN must be between 1 and 500.").ToJson();

        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            // topN is range-validated above — safe to interpolate
            var sql = $"""
                WITH Waits AS (
                    SELECT
                        wait_type,
                        wait_time_ms / 1000.0                       AS wait_seconds,
                        (wait_time_ms - signal_wait_time_ms) / 1000.0
                                                                    AS resource_wait_seconds,
                        signal_wait_time_ms / 1000.0                AS signal_wait_seconds,
                        waiting_tasks_count,
                        CASE WHEN waiting_tasks_count > 0
                             THEN CAST(wait_time_ms AS FLOAT) / waiting_tasks_count
                             ELSE 0
                        END                                         AS avg_wait_ms,
                        100.0 * wait_time_ms
                            / NULLIF(SUM(wait_time_ms) OVER (), 0) AS pct_total
                    FROM sys.dm_os_wait_stats
                    WHERE waiting_tasks_count > 0
                      AND (@ExcludeIdle = 0 OR wait_type NOT IN (
                            'SLEEP_TASK','BROKER_TO_FLUSH','BROKER_TASK_STOP',
                            'CLR_AUTO_EVENT','CLR_MANUAL_EVENT',
                            'DISPATCHER_QUEUE_SEMAPHORE',
                            'FT_IFTS_SCHEDULER_IDLE_WAIT',
                            'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
                            'HADR_WORK_QUEUE','LAZYWRITER_SLEEP',
                            'LOGMGR_QUEUE','ONDEMAND_TASK_QUEUE',
                            'REQUEST_FOR_DEADLOCK_MONITOR','RESOURCE_QUEUE',
                            'SERVER_IDLE_CHECK','SLEEP_DBSTARTUP',
                            'SLEEP_DBRECOVER','SLEEP_DBREPLICATION',
                            'SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY',
                            'SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP',
                            'SLEEP_SYSTEMTASK','SLEEP_TEMPDBSTARTUP',
                            'SNI_HTTP_ACCEPT','SP_SERVER_DIAGNOSTICS_SLEEP',
                            'SQLTRACE_BUFFER_FLUSH',
                            'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
                            'WAITFOR','WAIT_XTP_OFFLINE_CKPT_NEW_LOG',
                            'XE_DISPATCHER_WAIT','XE_TIMER_EVENT',
                            'BROKER_EVENTHANDLER','CHECKPOINT_QUEUE',
                            'DBMIRROR_EVENTS_QUEUE','SQLTRACE_WAIT_ENTRIES',
                            'WAIT_XTP_CKPT_CLOSE'))
                )
                SELECT TOP ({topN}) *
                FROM Waits
                ORDER BY wait_seconds DESC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@ExcludeIdle"] = excludeIdle ? 1 : 0 });

            return ToolResult.Ok(new { waitTypeCount = rows.Count, waits = rows }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Query the Query Store for the top resource-consuming queries over a recent time window. " +
        "Requires SQL Server 2016+ with Query Store enabled on the target database. " +
        "Returns avg/max CPU, duration, logical reads, execution count, and query text. " +
        "Unlike DMV-based tools, Query Store data survives server restarts.")]
    public async Task<string> get_query_store_top_queries(
        [Description("Name of the database (must have Query Store enabled).")]
        string databaseName,
        [Description("Maximum number of queries to return. Defaults to 25.")]
        int topN = 25,
        [Description("Sort order. One of: avg_cpu_time (default), avg_duration, execution_count, avg_logical_io_reads.")]
        string orderBy = "avg_cpu_time",
        [Description("Only include queries executed within the last N hours. Defaults to 24.")]
        int lastHours = 24,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        if (topN <= 0 || topN > 1000)
            return ToolResult.Fail("topN must be between 1 and 1000.").ToJson();

        if (!ValidOrderByColumns.Contains(orderBy))
            return ToolResult.Fail(
                $"Invalid orderBy '{orderBy}'. Must be one of: " +
                string.Join(", ", ValidOrderByColumns) + ".").ToJson();

        if (lastHours <= 0 || lastHours > 8760)
            return ToolResult.Fail("lastHours must be between 1 and 8760.").ToJson();

        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            // topN is range-validated — safe to interpolate.
            // orderBy is validated against an allowlist — safe to interpolate.
            // The CASE in ORDER BY is required because SQL Server cannot ORDER BY a
            // parameterized expression, and column aliases are not allowed in ORDER BY
            // for aggregated CTEs. Validated allowlist prevents injection.
            var sql = $"""
                SELECT TOP ({topN})
                    q.query_id,
                    qt.query_sql_text,
                    qsp.plan_id,
                    rs.count_executions,
                    CAST(rs.avg_cpu_time       / 1000.0 AS DECIMAL(18,2)) AS avg_cpu_ms,
                    CAST(rs.max_cpu_time       / 1000.0 AS DECIMAL(18,2)) AS max_cpu_ms,
                    CAST(rs.avg_duration       / 1000.0 AS DECIMAL(18,2)) AS avg_duration_ms,
                    CAST(rs.max_duration       / 1000.0 AS DECIMAL(18,2)) AS max_duration_ms,
                    rs.avg_logical_io_reads,
                    rs.max_logical_io_reads,
                    rs.avg_rowcount,
                    rs.last_execution_time,
                    q.last_compile_start_time
                FROM sys.query_store_query q
                JOIN sys.query_store_query_text qt
                    ON qt.query_text_id = q.query_text_id
                JOIN sys.query_store_plan qsp
                    ON qsp.query_id = q.query_id
                JOIN sys.query_store_runtime_stats rs
                    ON rs.plan_id = qsp.plan_id
                JOIN sys.query_store_runtime_stats_interval rsi
                    ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
                WHERE rsi.start_time >= DATEADD(HOUR, -@LastHours, GETUTCDATE())
                  AND q.is_internal_query = 0
                ORDER BY
                    CASE '{orderBy}'
                        WHEN 'avg_cpu_time'         THEN rs.avg_cpu_time
                        WHEN 'avg_duration'         THEN rs.avg_duration
                        WHEN 'execution_count'      THEN rs.count_executions
                        WHEN 'avg_logical_io_reads' THEN rs.avg_logical_io_reads
                        ELSE rs.avg_cpu_time
                    END DESC;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@LastHours"] = lastHours });

            return ToolResult.Ok(new
            {
                database   = databaseName,
                orderBy,
                lastHours,
                queryCount = rows.Count,
                queries    = rows
            }).ToJson();
        }
        catch (SqlException ex) when (ex.Message.Contains("query_store_query"))
        {
            return ToolResult.Fail(
                $"Query Store is not enabled on database '{databaseName}'. " +
                "Enable it with: ALTER DATABASE [{databaseName}] SET QUERY_STORE = ON;").ToJson();
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
