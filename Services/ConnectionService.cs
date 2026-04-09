using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQL_Server_MCP.Services;

public sealed class ConnectionService(ILogger<ConnectionService> logger)
{
    public const string EnvVarName = "SQL_SERVER_MCP_CONNECTION_STRING";

    public string Resolve(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        throw new InvalidOperationException(
            $"No connection string provided. Pass a 'connectionString' parameter or " +
            $"set the {EnvVarName} environment variable.");
    }

    public async Task<SqlConnection> OpenAsync(
        string? connectionString,
        string? databaseOverride = null,
        CancellationToken ct = default)
    {
        var cs = Resolve(connectionString);

        if (databaseOverride is not null)
        {
            var csb = new SqlConnectionStringBuilder(cs)
            {
                InitialCatalog = databaseOverride
            };
            cs = csb.ConnectionString;
        }

        var conn = new SqlConnection(cs);
        try
        {
            await conn.OpenAsync(ct);
            logger.LogDebug("Opened connection to {Server}/{Database}",
                conn.DataSource, conn.Database);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
