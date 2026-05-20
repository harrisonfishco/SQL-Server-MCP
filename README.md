# SQL Server MCP

A [Model Context Protocol](https://modelcontextprotocol.io) server that gives Claude deep visibility into SQL Server. Query your databases, diagnose performance problems, inspect locks and transactions, and explore schema — all through natural language.

Built with **.NET 8** and **Microsoft.Data.SqlClient**. Communicates over stdio so it works with Claude Desktop, Claude Code, and any MCP-compatible client.

---

## Features

- **Query execution** — run arbitrary T-SQL and get results as structured JSON
- **Execution plans** — retrieve estimated plans without executing, or actual plans with runtime statistics
- **Lock analysis** — see every current lock and trace full blocking chains with session context
- **Transaction diagnostics** — find open transactions, measure their age, see what they've locked, and spot log growth risks
- **Session monitoring** — list running queries with wait types, CPU, memory grants, and live plan XML
- **Schema exploration** — browse databases, tables, columns, indexes, stored procedures, views, and object definitions
- **Index health** — surface missing indexes with suggested `CREATE INDEX` statements, find unused indexes, measure fragmentation with rebuild/reorganize recommendations
- **Wait statistics** — identify server-wide bottlenecks from cumulative wait data
- **Query Store** — analyze historical top queries by CPU, duration, or I/O (SQL Server 2016+)

---

## Tools

### Query

| Tool | Description |
|---|---|
| `execute_query` | Execute T-SQL and return results as JSON. SELECT returns rows; DML returns rows affected. |
| `get_estimated_plan` | Return the XML execution plan without running the query (`SET SHOWPLAN_XML ON`). Safe for production — no locks, no data read. |
| `get_actual_plan` | Execute a query and capture the actual runtime XML plan alongside results (`SET STATISTICS XML ON`). |

### Sessions

| Tool | Description |
|---|---|
| `get_running_queries` | List executing requests with elapsed time, wait type, blocking session, CPU, logical reads, full SQL text, current statement, and live query plan XML. |
| `get_session_detail` | Full detail for a single session: isolation level, open transactions, memory, tempdb usage, connection info, and current SQL. |
| `kill_session` | Kill a session (SPID) or check rollback progress with `WITH STATUSONLY`. |

### Locks

| Tool | Description |
|---|---|
| `get_locks` | All current locks with resource type, mode, status, and owning session info. Filter by database. |
| `get_blocking_chains` | Recursive blocking hierarchy showing root blockers and all dependent blocked sessions with `chain_path` (e.g. `52 -> 67 -> 91`). |

### Transactions

| Tool | Description |
|---|---|
| `get_open_transactions` | All open transactions with age, isolation level, log bytes used, and current SQL. Filter by minimum age. |
| `get_transaction_locks` | All locks held by a specific transaction ID. |

### Schema

| Tool | Description |
|---|---|
| `list_databases` | All online databases with size, state, recovery model, and compatibility level. |
| `list_tables` | Tables in a database with estimated row counts and space usage. |
| `get_table_schema` | Full table schema: columns with types/defaults/nullability, PK and unique constraints, and foreign keys. |
| `list_indexes` | Indexes on a table or all tables, with key columns, included columns, and properties. |
| `list_stored_procs` | Stored procedures with execution count, last run time, and cumulative CPU from the plan cache. |
| `list_views` | Views, flagging which are indexed (materialized). |
| `get_object_definition` | Source T-SQL for a stored procedure, view, function, or trigger. |

### Index Health

| Tool | Description |
|---|---|
| `get_missing_indexes` | Missing index recommendations from DMVs, ranked by impact score, with ready-to-run `CREATE INDEX` statements. |
| `get_index_usage_stats` | Per-index seeks, scans, lookups, and updates since last restart. Find unused indexes wasting write overhead. |
| `get_index_fragmentation` | Fragmentation percentage with a `recommended_action` (OK / REORGANIZE / REBUILD). Supports LIMITED, SAMPLED, and DETAILED scan modes. |
| `get_wait_statistics` | Cumulative wait stats since last restart with idle waits filtered out. `pct_total` shows each type's share of real wait time. |
| `get_query_store_top_queries` | Historical top queries from Query Store (SQL Server 2016+), sortable by CPU, duration, execution count, or logical reads. |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- SQL Server 2016 or later (2019+ recommended for Query Store features)
- A SQL Server login with at minimum:
  - `VIEW SERVER STATE` — required for DMV access (sessions, locks, waits, requests)
  - `VIEW DATABASE STATE` — required for partition stats and index DMVs
  - `VIEW DEFINITION` — required for schema tools
  - `db_datareader` on target databases — required for `execute_query`

> **Note:** `kill_session` requires `ALTER ANY CONNECTION` or `sysadmin`. Execution plan tools require `SHOWPLAN` permission.

---

## Installation

### Build from source

```bash
git clone https://github.com/harrisonfishco/SQL-Server-MCP.git
cd SQL-Server-MCP
dotnet build -c Release
```

The compiled binary will be at:
```
bin/Release/net8.0/SQL-Server-MCP.exe        # Windows
bin/Release/net8.0/SQL-Server-MCP            # Linux / macOS
```

### Docker Hub

The easiest way to get started — no build required:

```bash
docker pull harrisonfishco/sql-server-mcp
```

### Build from Docker

```bash
docker build -t sql-server-mcp .
```

---

## Configuration

### Connection String

Every tool accepts an optional `connectionString` parameter. When omitted, the server falls back to the `SQL_SERVER_MCP_CONNECTION_STRING` environment variable.

**Standard authentication:**
```
Server=myserver;Database=mydb;User Id=sa;Password=secret;TrustServerCertificate=true;
```

**Windows integrated authentication:**
```
Server=myserver;Database=mydb;Integrated Security=true;
```

**Azure SQL with Entra ID:**
```
Server=myserver.database.windows.net;Database=mydb;Authentication=Active Directory Default;
```

Setting the environment variable lets you avoid passing credentials on every tool call:

```bash
export SQL_SERVER_MCP_CONNECTION_STRING="Server=myserver;Database=mydb;Integrated Security=true;"
```

---

## Claude Desktop Setup

Add the server to your `claude_desktop_config.json`:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`  
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "sql-server": {
      "command": "C:\\path\\to\\SQL-Server-MCP\\bin\\Release\\net8.0\\SQL-Server-MCP.exe",
      "env": {
        "SQL_SERVER_MCP_CONNECTION_STRING": "Server=myserver;Database=mydb;Integrated Security=true;"
      }
    }
  }
}
```

Restart Claude Desktop after saving.

### Docker Hub variant

```json
{
  "mcpServers": {
    "sql-server": {
      "command": "docker",
      "args": ["run", "--rm", "-i", "harrisonfishco/sql-server-mcp"],
      "env": {
        "SQL_SERVER_MCP_CONNECTION_STRING": "Server=host.docker.internal;Database=mydb;User Id=sa;Password=secret;TrustServerCertificate=true;"
      }
    }
  }
}
```

---

## Claude Code Setup

**From Docker Hub:**
```bash
claude mcp add sql-server \
  -e SQL_SERVER_MCP_CONNECTION_STRING="Server=myserver;Database=mydb;Integrated Security=true;" \
  -- docker run --rm -i harrisonfishco/sql-server-mcp
```

**From binary:**
```bash
claude mcp add sql-server \
  -e SQL_SERVER_MCP_CONNECTION_STRING="Server=myserver;Database=mydb;Integrated Security=true;" \
  -- /path/to/SQL-Server-MCP/bin/Release/net8.0/SQL-Server-MCP
```

---

## Example Prompts

Once connected, ask Claude things like:

**Performance diagnosis:**
> "Are there any blocking chains right now? Show me what's blocking what."

> "Find all queries that have been running for more than 30 seconds and show me their execution plans."

> "What are the top 10 wait types on this server? What do they indicate?"

**Transaction investigation:**
> "Show me all open transactions older than 5 minutes. What are they holding locks on?"

> "There's a transaction with ID 12345 — what tables does it have locked?"

**Query tuning:**
> "Get the estimated execution plan for this query and tell me what indexes it's missing: SELECT * FROM Orders WHERE CustomerId = 42 AND Status = 'Pending'"

> "What are the top 5 queries by average CPU time in the last 6 hours?"

> "Which indexes on the Sales database have never been used but are still being maintained on writes?"

**Schema exploration:**
> "Show me the full schema for the Orders table including foreign keys and constraints."

> "List all stored procedures in the dbo schema that match the pattern 'usp_Order%' and show their last execution times."

> "What's the source code of the usp_GetCustomerOrders procedure?"

**Index health:**
> "What indexes does SQL Server recommend I add to the Reporting database? Give me the CREATE INDEX statements."

> "Show me index fragmentation for the Orders table and tell me which ones need rebuilding."

---

## Architecture

```
SQL-Server-MCP/
├── Program.cs                  # MCP server bootstrap (stdio transport)
│
├── Services/
│   ├── ConnectionService.cs    # Resolves connection string, opens SqlConnection,
│   │                           # patches Initial Catalog for database-scoped tools
│   └── QueryExecutor.cs        # Executes SQL, returns List<Dictionary<string,object?>>
│                               # or raw strings for XML plans; handles multi-result sets
│
├── Models/
│   └── ToolResult.cs           # Unified { success, data, error } JSON envelope
│                               # returned by every tool method
│
└── Tools/
    ├── QueryTools.cs           # execute_query, get_estimated_plan, get_actual_plan
    ├── SessionTools.cs         # get_running_queries, get_session_detail, kill_session
    ├── LockTools.cs            # get_locks, get_blocking_chains
    ├── TransactionTools.cs     # get_open_transactions, get_transaction_locks
    ├── SchemaTools.cs          # list_databases, list_tables, get_table_schema,
    │                           # list_indexes, list_stored_procs, list_views,
    │                           # get_object_definition
    └── IndexHealthTools.cs     # get_missing_indexes, get_index_usage_stats,
                                # get_index_fragmentation, get_wait_statistics,
                                # get_query_store_top_queries
```

### Response format

Every tool returns a JSON string with this shape:

```json
{
  "success": true,
  "data": "...",
  "error": null
}
```

On failure, `success` is `false`, `data` is `"[]"`, and `error` contains a human-readable message including the SQL error number, severity, and state where applicable.

### Security

User-supplied identifiers that must be embedded in SQL (database names, schema names, table names) are always scoped via `SqlConnectionStringBuilder.InitialCatalog` or passed as `@parameters` — never string-concatenated raw. The only exceptions are values validated against a strict allowlist before interpolation:

- `sampleMode` in `get_index_fragmentation` — validated against `{ LIMITED, SAMPLED, DETAILED }`
- `orderBy` in `get_query_store_top_queries` — validated against `{ avg_cpu_time, avg_duration, execution_count, avg_logical_io_reads }`
- `sessionId` in `kill_session` — parsed to `int` and range-checked (1–32767) before interpolation, since `KILL` does not accept parameterized input

---

## DMV Reference

The following SQL Server DMVs and system catalog views are used:

| DMV / View | Used by |
|---|---|
| `sys.dm_exec_requests` | `get_running_queries`, `get_blocking_chains`, `get_open_transactions` |
| `sys.dm_exec_sessions` | `get_running_queries`, `get_session_detail`, `get_locks`, `get_open_transactions` |
| `sys.dm_exec_connections` | `get_session_detail` |
| `sys.dm_exec_sql_text` | `get_running_queries`, `get_session_detail`, `get_blocking_chains`, `get_open_transactions` |
| `sys.dm_exec_query_plan` | `get_running_queries`, `get_blocking_chains` |
| `sys.dm_tran_locks` | `get_locks`, `get_transaction_locks` |
| `sys.dm_tran_active_transactions` | `get_open_transactions` |
| `sys.dm_tran_session_transactions` | `get_open_transactions` |
| `sys.dm_tran_database_transactions` | `get_open_transactions` |
| `sys.dm_os_wait_stats` | `get_wait_statistics` |
| `sys.dm_db_missing_index_details/groups/group_stats` | `get_missing_indexes` |
| `sys.dm_db_index_usage_stats` | `get_index_usage_stats` |
| `sys.dm_db_index_physical_stats` | `get_index_fragmentation` |
| `sys.dm_db_partition_stats` | `list_tables` |
| `sys.dm_db_session_space_usage` | `get_session_detail` |
| `sys.dm_exec_procedure_stats` | `list_stored_procs` |
| `sys.query_store_*` | `get_query_store_top_queries` |
| `sys.databases`, `sys.master_files` | `list_databases` |
| `sys.tables`, `sys.columns`, `sys.indexes`, etc. | Schema tools |

---

## Permissions Reference

Minimum permissions needed per tool category:

```sql
-- DMV access (sessions, locks, waits, running queries)
GRANT VIEW SERVER STATE TO [your_login];

-- Schema exploration and object definitions
GRANT VIEW DEFINITION ON DATABASE::YourDatabase TO [your_login];

-- Table row counts and space usage
GRANT VIEW DATABASE STATE TO [your_login];

-- Read data with execute_query
ALTER ROLE db_datareader ADD MEMBER [your_login];

-- Execution plans
GRANT SHOWPLAN TO [your_login];

-- Kill sessions (use a dedicated admin login for this)
GRANT ALTER ANY CONNECTION TO [your_login];
```

---

## Compatibility

| Feature | Minimum Version |
|---|---|
| All core tools | SQL Server 2016 |
| `get_query_store_top_queries` | SQL Server 2016 (Query Store must be enabled) |
| `STRING_AGG` in index/schema queries | SQL Server 2017 |
| Azure SQL Database | Supported |
| Azure SQL Managed Instance | Supported |

---

## License

MIT
