using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SQL_Server_MCP.Services;

public sealed class QueryExecutor(ILogger<QueryExecutor> logger)
{
    public const int DefaultTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Executes a query and returns all rows as a list of column-name → value dictionaries.
    /// DBNull values are returned as null. Non-SELECT statements return a single row
    /// with a "rowsAffected" key.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ExecuteAsync(
        SqlConnection conn,
        string sql,
        Dictionary<string, object?>? parameters = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken ct = default)
    {
        await using var cmd = BuildCommand(conn, sql, parameters, timeoutSeconds);

        logger.LogDebug("Executing query (timeout={Timeout}s): {Sql}", timeoutSeconds, sql);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Non-SELECT: no columns in the result set
        if (reader.FieldCount == 0)
        {
            var affected = reader.RecordsAffected;
            return [new Dictionary<string, object?> { ["rowsAffected"] = affected }];
        }

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        logger.LogDebug("Query returned {RowCount} rows", rows.Count);
        return rows;
    }

    /// <summary>
    /// Executes a query and returns the first column of the first row as a string.
    /// Used for XML execution plans which come back as a single XML cell.
    /// </summary>
    public async Task<string?> ExecuteScalarAsync(
        SqlConnection conn,
        string sql,
        Dictionary<string, object?>? parameters = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken ct = default)
    {
        await using var cmd = BuildCommand(conn, sql, parameters, timeoutSeconds);

        logger.LogDebug("Executing scalar query (timeout={Timeout}s)", timeoutSeconds);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : result.ToString();
    }

    /// <summary>
    /// Executes a query, reads the first result set as rows, then advances to the next
    /// result set and reads the first column of the first row as a string.
    /// Used for actual execution plans (STATISTICS XML ON appends a second result set).
    /// </summary>
    public async Task<(List<Dictionary<string, object?>> Rows, string? Plan)> ExecuteWithPlanAsync(
        SqlConnection conn,
        string sql,
        Dictionary<string, object?>? parameters = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken ct = default)
    {
        await using var cmd = BuildCommand(conn, sql, parameters, timeoutSeconds);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // First result set: query data
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        // Second result set: XML plan
        string? plan = null;
        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
            plan = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();

        return (rows, plan);
    }

    /// <summary>
    /// Serializes a list of row dictionaries to a compact JSON array string.
    /// </summary>
    public string Serialize(List<Dictionary<string, object?>> rows) =>
        JsonSerializer.Serialize(rows, SerializerOptions);

    private static SqlCommand BuildCommand(
        SqlConnection conn,
        string sql,
        Dictionary<string, object?>? parameters,
        int timeoutSeconds)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }
}
