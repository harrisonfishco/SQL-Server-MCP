using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_Server_MCP.Models;
using SQL_Server_MCP.Services;
using System.ComponentModel;

namespace SQL_Server_MCP.Tools;

[McpServerToolType]
public sealed class SchemaTools(ConnectionService connectionService, QueryExecutor queryExecutor)
{
    [McpServerTool, Description(
        "List all online databases on the server with state, size, recovery model, and compatibility level.")]
    public async Task<string> list_databases(
        [Description("Include system databases (master, msdb, model, tempdb). Defaults to false.")]
        bool includeSystemDatabases = false,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString);

            const string sql = """
                SELECT
                    d.database_id,
                    d.name,
                    d.state_desc,
                    d.compatibility_level,
                    d.recovery_model_desc,
                    d.collation_name,
                    d.create_date,
                    d.is_read_only,
                    d.is_auto_close_on,
                    d.is_auto_shrink_on,
                    SUM(mf.size) * 8 / 1024             AS total_size_mb
                FROM sys.databases d
                JOIN sys.master_files mf ON mf.database_id = d.database_id
                WHERE d.state = 0
                  AND (@IncludeSystem = 1
                       OR d.name NOT IN ('master', 'msdb', 'model', 'tempdb'))
                GROUP BY
                    d.database_id, d.name, d.state_desc, d.compatibility_level,
                    d.recovery_model_desc, d.collation_name, d.create_date,
                    d.is_read_only, d.is_auto_close_on, d.is_auto_shrink_on
                ORDER BY d.name;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@IncludeSystem"] = includeSystemDatabases ? 1 : 0 });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "List all tables in a database with estimated row counts and space usage. " +
        "Requires VIEW DATABASE STATE permission.")]
    public async Task<string> list_tables(
        [Description("Name of the database to list tables from.")]
        string databaseName,
        [Description("Filter to a specific schema (e.g. 'dbo'). Omit for all schemas.")]
        string? schemaName = null,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            const string sql = """
                SELECT
                    s.name                              AS schema_name,
                    t.name                              AS table_name,
                    t.object_id,
                    t.create_date,
                    t.modify_date,
                    t.is_memory_optimized,
                    t.temporal_type_desc,
                    SUM(ps.row_count)                   AS row_count_estimate,
                    SUM(ps.reserved_page_count) * 8     AS total_space_kb,
                    SUM(ps.used_page_count) * 8         AS used_space_kb
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.dm_db_partition_stats ps
                    ON ps.object_id = t.object_id AND ps.index_id IN (0, 1)
                WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
                GROUP BY
                    s.name, t.name, t.object_id, t.create_date, t.modify_date,
                    t.is_memory_optimized, t.temporal_type_desc
                ORDER BY s.name, t.name;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@SchemaName"] = schemaName });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Get the full schema of a table: columns with types, nullability, defaults, and descriptions; " +
        "primary key and unique constraints; and foreign key relationships.")]
    public async Task<string> get_table_schema(
        [Description("Name of the database.")]
        string databaseName,
        [Description("Name of the table.")]
        string tableName,
        [Description("Schema name. Defaults to 'dbo'.")]
        string schemaName = "dbo",
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            var parameters = new Dictionary<string, object?>
            {
                ["@TableName"] = tableName,
                ["@SchemaName"] = schemaName
            };

            const string columnsSql = """
                SELECT
                    c.column_id,
                    c.name                              AS column_name,
                    tp.name                             AS data_type,
                    c.max_length,
                    c.precision,
                    c.scale,
                    c.is_nullable,
                    c.is_identity,
                    c.is_computed,
                    cc.definition                       AS computed_definition,
                    dc.definition                       AS default_value,
                    CAST(ep.value AS NVARCHAR(MAX))     AS description
                FROM sys.columns c
                JOIN sys.types tp ON tp.user_type_id = c.user_type_id
                JOIN sys.objects o ON o.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                LEFT JOIN sys.computed_columns cc
                    ON cc.object_id = c.object_id AND cc.column_id = c.column_id
                LEFT JOIN sys.default_constraints dc
                    ON dc.parent_object_id = c.object_id
                    AND dc.parent_column_id = c.column_id
                LEFT JOIN sys.extended_properties ep
                    ON ep.major_id = c.object_id
                    AND ep.minor_id = c.column_id
                    AND ep.name = 'MS_Description'
                    AND ep.class = 1
                WHERE o.name = @TableName AND s.name = @SchemaName
                ORDER BY c.column_id;
                """;

            const string constraintsSql = """
                SELECT
                    i.name                              AS constraint_name,
                    i.type_desc                         AS index_type,
                    CASE i.is_primary_key
                        WHEN 1 THEN 'PRIMARY KEY' ELSE 'UNIQUE'
                    END                                 AS constraint_type,
                    STRING_AGG(c.name, ', ')
                        WITHIN GROUP (ORDER BY ic.key_ordinal) AS columns
                FROM sys.indexes i
                JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
                  AND o.name = @TableName AND s.name = @SchemaName
                GROUP BY i.name, i.type_desc, i.is_primary_key;
                """;

            const string fkSql = """
                SELECT
                    fk.name                             AS fk_name,
                    COL_NAME(fkc.parent_object_id,
                             fkc.parent_column_id)      AS column_name,
                    OBJECT_SCHEMA_NAME(
                        fk.referenced_object_id)        AS ref_schema,
                    OBJECT_NAME(
                        fk.referenced_object_id)        AS ref_table,
                    COL_NAME(fkc.referenced_object_id,
                             fkc.referenced_column_id)  AS ref_column,
                    fk.delete_referential_action_desc,
                    fk.update_referential_action_desc
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc
                    ON fkc.constraint_object_id = fk.object_id
                JOIN sys.objects o ON o.object_id = fk.parent_object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.name = @TableName AND s.name = @SchemaName;
                """;

            var columns = await queryExecutor.ExecuteAsync(conn, columnsSql, parameters);
            var constraints = await queryExecutor.ExecuteAsync(conn, constraintsSql, parameters);
            var foreignKeys = await queryExecutor.ExecuteAsync(conn, fkSql, parameters);

            return ToolResult.Ok(new { columns, constraints, foreignKeys }).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "List indexes on a specific table, or all user tables in a database. " +
        "Shows key columns, included columns, uniqueness, fill factor, and filter definition.")]
    public async Task<string> list_indexes(
        [Description("Name of the database.")]
        string databaseName,
        [Description("Filter to a specific table. Omit to return indexes for all tables.")]
        string? tableName = null,
        [Description("Schema name used when tableName is supplied. Defaults to 'dbo'.")]
        string schemaName = "dbo",
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
                    i.index_id,
                    i.name                              AS index_name,
                    i.type_desc                         AS index_type,
                    i.is_unique,
                    i.is_primary_key,
                    i.is_unique_constraint,
                    i.is_disabled,
                    i.fill_factor,
                    i.has_filter,
                    i.filter_definition,
                    STRING_AGG(
                        CASE WHEN ic.is_included_column = 0 THEN c.name ELSE NULL END,
                        ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS key_columns,
                    STRING_AGG(
                        CASE WHEN ic.is_included_column = 1 THEN c.name ELSE NULL END,
                        ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS included_columns
                FROM sys.indexes i
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE o.type = 'U'
                  AND i.type > 0
                  AND (@TableName IS NULL
                       OR (o.name = @TableName AND s.name = @SchemaName))
                GROUP BY
                    s.name, o.name, i.index_id, i.name, i.type_desc,
                    i.is_unique, i.is_primary_key, i.is_unique_constraint,
                    i.is_disabled, i.fill_factor, i.has_filter, i.filter_definition
                ORDER BY s.name, o.name, i.index_id;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@TableName"] = tableName,
                    ["@SchemaName"] = schemaName
                });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "List stored procedures in a database with creation/modify dates and cumulative " +
        "execution count, CPU, and elapsed time stats from the plan cache (since last restart).")]
    public async Task<string> list_stored_procs(
        [Description("Name of the database.")]
        string databaseName,
        [Description("Filter to a specific schema. Omit for all schemas.")]
        string? schemaName = null,
        [Description("LIKE pattern to filter procedure names, e.g. 'usp_%'. Omit for all procedures.")]
        string? nameFilter = null,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            const string sql = """
                SELECT
                    s.name                              AS schema_name,
                    p.name                              AS proc_name,
                    p.object_id,
                    p.create_date,
                    p.modify_date,
                    ps.execution_count,
                    ps.last_execution_time,
                    ps.total_worker_time / 1000         AS total_cpu_ms,
                    ps.total_elapsed_time / 1000        AS total_elapsed_ms,
                    (SELECT COUNT(*)
                     FROM sys.parameters pm
                     WHERE pm.object_id = p.object_id
                       AND pm.parameter_id > 0)         AS parameter_count
                FROM sys.procedures p
                JOIN sys.schemas s ON s.schema_id = p.schema_id
                LEFT JOIN sys.dm_exec_procedure_stats ps ON ps.object_id = p.object_id
                WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
                  AND (@NameFilter IS NULL OR p.name LIKE @NameFilter)
                ORDER BY s.name, p.name;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@SchemaName"] = schemaName,
                    ["@NameFilter"] = nameFilter
                });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "List views in a database, indicating which are indexed (materialized) views.")]
    public async Task<string> list_views(
        [Description("Name of the database.")]
        string databaseName,
        [Description("Filter to a specific schema. Omit for all schemas.")]
        string? schemaName = null,
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            const string sql = """
                SELECT
                    s.name                              AS schema_name,
                    v.name                              AS view_name,
                    v.object_id,
                    v.create_date,
                    v.modify_date,
                    v.is_replicated,
                    CASE WHEN EXISTS (
                        SELECT 1 FROM sys.indexes i
                        WHERE i.object_id = v.object_id AND i.index_id = 1
                    ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS is_indexed_view
                FROM sys.views v
                JOIN sys.schemas s ON s.schema_id = v.schema_id
                WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
                ORDER BY s.name, v.name;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?> { ["@SchemaName"] = schemaName });

            return ToolResult.Ok(queryExecutor.Serialize(rows)).ToJson();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(FormatException(ex)).ToJson();
        }
    }

    [McpServerTool, Description(
        "Return the T-SQL source definition of a stored procedure, view, function, or trigger.")]
    public async Task<string> get_object_definition(
        [Description("Name of the database.")]
        string databaseName,
        [Description("Name of the object (procedure, view, function, or trigger).")]
        string objectName,
        [Description("Schema name. Defaults to 'dbo'.")]
        string schemaName = "dbo",
        [Description("ADO.NET connection string. Falls back to SQL_SERVER_MCP_CONNECTION_STRING env var.")]
        string? connectionString = null)
    {
        try
        {
            await using var conn = await connectionService.OpenAsync(connectionString, databaseName);

            const string sql = """
                SELECT
                    OBJECT_DEFINITION(
                        OBJECT_ID(QUOTENAME(@SchemaName) + '.' + QUOTENAME(@ObjectName))
                    )                                   AS definition,
                    OBJECT_ID(
                        QUOTENAME(@SchemaName) + '.' + QUOTENAME(@ObjectName)
                    )                                   AS object_id;
                """;

            var rows = await queryExecutor.ExecuteAsync(conn, sql,
                new Dictionary<string, object?>
                {
                    ["@ObjectName"] = objectName,
                    ["@SchemaName"] = schemaName
                });

            if (rows.Count == 0 || rows[0]["object_id"] is null)
                return ToolResult.Fail(
                    $"Object '{schemaName}.{objectName}' not found in database '{databaseName}'.").ToJson();

            return ToolResult.Ok(new
            {
                objectName = $"{schemaName}.{objectName}",
                definition = rows[0]["definition"]?.ToString()
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
