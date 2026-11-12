using Microsoft.Data.Sqlite;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Core.Storage;

public sealed record JobEventRecord
{
    public long Id { get; init; }
    public required string JobId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "Information";
    public string Message { get; init; } = "";
}

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default);
    Task<JobDetail?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<JobDetail?> GetActiveJobAsync(string agentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(string agentId, int limit = 20, CancellationToken cancellationToken = default);
    Task AppendEventAsync(string jobId, string level, string message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobEventRecord>> GetEventsAsync(string jobId, int limit = 200, CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);
}

/// <summary>에이전트 로컬 SQLite 저장소. LabManager가 원본이고 이곳은 캐시다.</summary>
public sealed class SqliteJobStore : IJobStore
{
    private readonly string _connectionString;

    public SqliteJobStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(UpdateAgent.Shared.UpdateAgentConstants.DataDirectory, "agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS jobs (
                job_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                state INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                job_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_agent_state ON jobs(agent_id, state);
            CREATE INDEX IF NOT EXISTS ix_jobs_updated ON jobs(updated_at DESC);

            CREATE TABLE IF NOT EXISTS job_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                ts TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_job_events_job ON job_events(job_id, id DESC);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, agent_id, state, updated_at, job_json)
            VALUES($id, $agent, $state, $updated, $json)
            ON CONFLICT(job_id) DO UPDATE SET
                agent_id = excluded.agent_id,
                state = excluded.state,
                updated_at = excluded.updated_at,
                job_json = excluded.job_json;
            """;
        command.Parameters.AddWithValue("$id", detail.Summary.JobId);
        command.Parameters.AddWithValue("$agent", detail.Summary.AgentId);
        command.Parameters.AddWithValue("$state", (int)detail.Summary.State);
        command.Parameters.AddWithValue("$updated", detail.Summary.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", UpdateAgentJson.Serialize(detail));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobDetail?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_json FROM jobs WHERE job_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", jobId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : UpdateAgentJson.Deserialize<JobDetail>(json);
    }

    public async Task<JobDetail?> GetActiveJobAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_json FROM jobs
            WHERE agent_id = $agent AND state NOT IN (8, 9, 10)
            ORDER BY updated_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : UpdateAgentJson.Deserialize<JobDetail>(json);
    }

    public async Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(
        string agentId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_json FROM jobs
            WHERE agent_id = $agent
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<JobSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var detail = UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(0));
            if (detail is not null) results.Add(detail.Summary);
        }

        return results;
    }

    public async Task AppendEventAsync(
        string jobId,
        string level,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_events(job_id, ts, level, message)
            VALUES($job, $ts, $level, $message);
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$message", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobEventRecord>> GetEventsAsync(
        string jobId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, ts, level, message FROM job_events
            WHERE job_id = $job ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<JobEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new JobEventRecord
            {
                Id = reader.GetInt64(0),
                JobId = reader.GetString(1),
                Timestamp = DateTimeOffset.Parse(reader.GetString(2)),
                Level = reader.GetString(3),
                Message = reader.GetString(4),
            });
        }

        return results;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value, updated_at) VALUES($key, $value, $ts)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
