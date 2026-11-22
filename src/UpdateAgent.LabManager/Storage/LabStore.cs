using Microsoft.Data.Sqlite;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Storage;

public sealed record AgentRecord
{
    public required AgentInfo Agent { get; init; }
    public required AgentTelemetry Telemetry { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

public sealed record LabJobRecord
{
    public required JobSummary Summary { get; init; }
    public JobDetail? Detail { get; init; }
    public string MachineName { get; init; } = "";
}

public sealed record AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Actor { get; init; } = "";
    public string Action { get; init; } = "";
    public string Target { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>교사 PC의 LabManager SQLite 저장소. 작업 상태의 원본이다.</summary>
public sealed class LabStore
{
    private readonly int _onlineThresholdSeconds;
    private readonly string _connectionString;

    public LabStore(string databasePath, int onlineThresholdSeconds = 90)
    {
        if (onlineThresholdSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(onlineThresholdSeconds));
        _onlineThresholdSeconds = onlineThresholdSeconds;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
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

            CREATE TABLE IF NOT EXISTS agents (
                agent_id TEXT PRIMARY KEY,
                machine_name TEXT NOT NULL,
                state INTEGER NOT NULL,
                last_seen TEXT NOT NULL,
                info_json TEXT NOT NULL,
                telemetry_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                job_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                state INTEGER NOT NULL,
                phase TEXT NOT NULL,
                progress INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                summary_json TEXT NOT NULL,
                detail_json TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_agent ON jobs(agent_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_jobs_updated ON jobs(updated_at DESC);

            CREATE TABLE IF NOT EXISTS commands (
                command_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                type INTEGER NOT NULL,
                status INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                issued_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                result_message TEXT,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_commands_agent_status ON commands(agent_id, status);

            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT NOT NULL,
                detail TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit(id DESC);

            CREATE TABLE IF NOT EXISTS maestro (
                job_id TEXT PRIMARY KEY,
                checklist_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------- Agents ----------

    public async Task UpsertAgentAsync(
        AgentInfo agent,
        AgentTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agents(agent_id, machine_name, state, last_seen, info_json, telemetry_json)
            VALUES($id, $name, $state, $seen, $info, $telemetry)
            ON CONFLICT(agent_id) DO UPDATE SET
                machine_name = excluded.machine_name,
                state = excluded.state,
                last_seen = excluded.last_seen,
                info_json = excluded.info_json,
                telemetry_json = excluded.telemetry_json;
            """;
        command.Parameters.AddWithValue("$id", agent.AgentId);
        command.Parameters.AddWithValue("$name", agent.MachineName);
        command.Parameters.AddWithValue("$state", (int)telemetry.State);
        command.Parameters.AddWithValue("$seen", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$info", UpdateAgentJson.Serialize(agent));
        // 사용자 표시 이름은 개인정보이므로 DB에 저장하지 않는다. 레거시 값도 여기서 제거한다.
        command.Parameters.AddWithValue("$telemetry",
            UpdateAgentJson.Serialize(telemetry with { LoggedOnUsers = [] }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>재이미징/폐기된 PC를 목록에서 제거한다. 작업 이력은 보존한다.</summary>
    public async Task<bool> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agents WHERE agent_id = $id;";
        command.Parameters.AddWithValue("$id", agentId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;

        await using var settingCommand = connection.CreateCommand();
        settingCommand.CommandText = "DELETE FROM settings WHERE key = $key;";
        settingCommand.Parameters.AddWithValue("$key", "agent-policy:" + agentId);
        await settingCommand.ExecuteNonQueryAsync(cancellationToken);
        return deleted;
    }

    public async Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT info_json, telemetry_json, last_seen FROM agents
            ORDER BY machine_name COLLATE NOCASE;
            """;

        var results = new List<AgentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var info = UpdateAgentJson.Deserialize<AgentInfo>(reader.GetString(0));
            var telemetry = UpdateAgentJson.Deserialize<AgentTelemetry>(reader.GetString(1));
            if (info is null || telemetry is null) continue;
            if (telemetry.LoggedOnUsers.Count > 0) telemetry = telemetry with { LoggedOnUsers = [] };

            var lastSeen = DateTimeOffset.TryParse(reader.GetString(2), out var parsed)
                ? parsed
                : info.LastSeen;

            results.Add(new AgentRecord
            {
                Agent = info,
                Telemetry = telemetry,
                LastSeen = lastSeen,
                Online = DateTimeOffset.Now - lastSeen < TimeSpan.FromSeconds(_onlineThresholdSeconds),
            });
        }

        return results;
    }

    public async Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var agents = await GetAgentsAsync(cancellationToken);
        return agents.FirstOrDefault(a =>
            string.Equals(a.Agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Jobs ----------

    public async Task SaveJobAsync(JobDetail detail, string machineName, CancellationToken cancellationToken = default)
    {
        var summary = detail.Summary;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, agent_id, machine_name, state, phase, progress,
                             created_at, updated_at, summary_json, detail_json)
            VALUES($id, $agent, $machine, $state, $phase, $progress, $created, $updated, $summary, $detail)
            ON CONFLICT(job_id) DO UPDATE SET
                state = excluded.state,
                phase = excluded.phase,
                progress = excluded.progress,
                updated_at = excluded.updated_at,
                summary_json = excluded.summary_json,
                detail_json = COALESCE(excluded.detail_json, jobs.detail_json);
            """;
        command.Parameters.AddWithValue("$id", summary.JobId);
        command.Parameters.AddWithValue("$agent", summary.AgentId);
        command.Parameters.AddWithValue("$machine", machineName);
        command.Parameters.AddWithValue("$state", (int)summary.State);
        command.Parameters.AddWithValue("$phase", summary.Phase);
        command.Parameters.AddWithValue("$progress", summary.ProgressPercent);
        command.Parameters.AddWithValue("$created", summary.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", summary.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$summary", UpdateAgentJson.Serialize(summary));
        command.Parameters.AddWithValue("$detail", UpdateAgentJson.Serialize(detail));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveJobSummaryAsync(
        JobSummary summary,
        string machineName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, agent_id, machine_name, state, phase, progress,
                             created_at, updated_at, summary_json, detail_json)
            VALUES($id, $agent, $machine, $state, $phase, $progress, $created, $updated, $summary, NULL)
            ON CONFLICT(job_id) DO UPDATE SET
                state = excluded.state,
                phase = excluded.phase,
                progress = excluded.progress,
                updated_at = excluded.updated_at,
                summary_json = excluded.summary_json;
            """;
        command.Parameters.AddWithValue("$id", summary.JobId);
        command.Parameters.AddWithValue("$agent", summary.AgentId);
        command.Parameters.AddWithValue("$machine", machineName);
        command.Parameters.AddWithValue("$state", (int)summary.State);
        command.Parameters.AddWithValue("$phase", summary.Phase);
        command.Parameters.AddWithValue("$progress", summary.ProgressPercent);
        command.Parameters.AddWithValue("$created", summary.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", summary.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$summary", UpdateAgentJson.Serialize(summary));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LabJobRecord>> GetJobsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json, detail_json, machine_name FROM jobs
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<LabJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
            if (summary is null) continue;

            var detail = reader.IsDBNull(1)
                ? null
                : UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(1));

            results.Add(new LabJobRecord
            {
                Summary = summary,
                Detail = detail,
                MachineName = reader.GetString(2),
            });
        }

        return results;
    }

    public async Task<LabJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json, detail_json, machine_name FROM jobs
            WHERE job_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
        if (summary is null) return null;

        return new LabJobRecord
        {
            Summary = summary,
            Detail = reader.IsDBNull(1)
                ? null
                : UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(1)),
            MachineName = reader.GetString(2),
        };
    }

    public async Task<int> CountActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM jobs
            WHERE state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$completed", (int)UpdateJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)UpdateJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)UpdateJobState.Cancelled);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    // ---------- Commands ----------

    public async Task AddCommandAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command2 = connection.CreateCommand();
        command2.CommandText = """
            INSERT INTO commands(command_id, agent_id, type, status, payload_json,
                                 issued_by, created_at, expires_at)
            VALUES($id, $agent, $type, $status, $payload, $issued, $created, $expires);
            """;
        command2.Parameters.AddWithValue("$id", command.CommandId);
        command2.Parameters.AddWithValue("$agent", command.AgentId);
        command2.Parameters.AddWithValue("$type", (int)command.Type);
        command2.Parameters.AddWithValue("$status", (int)command.Status);
        command2.Parameters.AddWithValue("$payload", command.PayloadJson);
        command2.Parameters.AddWithValue("$issued", command.IssuedBy);
        command2.Parameters.AddWithValue("$created", command.CreatedAt.ToString("O"));
        command2.Parameters.AddWithValue("$expires", command.ExpiresAt.ToString("O"));
        await command2.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCommand>> TakePendingCommandsAsync(
        string agentId,
        int max,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now.ToString("O");
        var results = new List<AgentCommand>();

        await using var connection = await OpenAsync(cancellationToken);
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT command_id, agent_id, type, status, payload_json, issued_by,
                       created_at, expires_at
                FROM commands
                WHERE agent_id = $agent
                  AND status IN ($pending, $delivered)
                  AND expires_at > $now
                ORDER BY created_at LIMIT $max;
                """;
            select.Parameters.AddWithValue("$agent", agentId);
            select.Parameters.AddWithValue("$pending", (int)CommandStatus.Pending);
            select.Parameters.AddWithValue("$delivered", (int)CommandStatus.Delivered);
            select.Parameters.AddWithValue("$now", now);
            select.Parameters.AddWithValue("$max", max);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new AgentCommand
                {
                    CommandId = reader.GetString(0),
                    AgentId = reader.GetString(1),
                    Type = (CommandType)reader.GetInt32(2),
                    Status = CommandStatus.Delivered,
                    PayloadJson = reader.GetString(4),
                    IssuedBy = reader.GetString(5),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
                    ExpiresAt = DateTimeOffset.Parse(reader.GetString(7)),
                });
            }
        }

        if (results.Count == 0) return results;

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE commands SET status = $delivered WHERE command_id = $id;";
        update.Parameters.AddWithValue("$delivered", (int)CommandStatus.Delivered);
        var idParam = update.Parameters.Add("$id", SqliteType.Text);

        foreach (var command in results)
        {
            idParam.Value = command.CommandId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return results;
    }

    public async Task CompleteCommandResultsAsync(
        string agentId,
        IEnumerable<CommandResult> results,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 공유 에이전트 토큰을 가진 다른 PC의 명령 결과로 상태를 바꿀 수 없도록 소유자를 함께 검사한다.
        command.CommandText = """
            UPDATE commands SET status = $status, result_message = $message, completed_at = $ts
            WHERE command_id = $id AND agent_id = $agent;
            """;
        var statusParam = command.Parameters.Add("$status", SqliteType.Integer);
        var messageParam = command.Parameters.Add("$message", SqliteType.Text);
        var tsParam = command.Parameters.Add("$ts", SqliteType.Text);
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var agentParam = command.Parameters.Add("$agent", SqliteType.Text);
        agentParam.Value = agentId;

        foreach (var result in results)
        {
            statusParam.Value = (int)(result.Success ? CommandStatus.Succeeded : CommandStatus.Failed);
            messageParam.Value = result.Message;
            tsParam.Value = result.CompletedAt.ToString("O");
            idParam.Value = result.CommandId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<AgentCommand?> GetCommandAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT command_id, agent_id, type, status, payload_json, issued_by,
                   created_at, expires_at, result_message, completed_at
            FROM commands WHERE command_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", commandId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AgentCommand
        {
            CommandId = reader.GetString(0),
            AgentId = reader.GetString(1),
            Type = (CommandType)reader.GetInt32(2),
            Status = (CommandStatus)reader.GetInt32(3),
            PayloadJson = reader.GetString(4),
            IssuedBy = reader.GetString(5),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
            ExpiresAt = DateTimeOffset.Parse(reader.GetString(7)),
            ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
            CompletedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
        };
    }

    public async Task<IReadOnlyList<AgentCommand>> GetCommandsAsync(
        string? agentId = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);        await using var command = connection.CreateCommand();
        command.CommandText = agentId is null
            ? """
              SELECT command_id, agent_id, type, status, payload_json, issued_by,
                     created_at, expires_at, result_message, completed_at
              FROM commands ORDER BY created_at DESC LIMIT $limit;
              """
            : """
              SELECT command_id, agent_id, type, status, payload_json, issued_by,
                     created_at, expires_at, result_message, completed_at
              FROM commands WHERE agent_id = $agent ORDER BY created_at DESC LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$limit", limit);
        if (agentId is not null) command.Parameters.AddWithValue("$agent", agentId);

        var results = new List<AgentCommand>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentCommand
            {
                CommandId = reader.GetString(0),
                AgentId = reader.GetString(1),
                Type = (CommandType)reader.GetInt32(2),
                Status = (CommandStatus)reader.GetInt32(3),
                PayloadJson = reader.GetString(4),
                IssuedBy = reader.GetString(5),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
                ExpiresAt = DateTimeOffset.Parse(reader.GetString(7)),
                ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                CompletedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            });
        }

        return results;
    }

    // ---------- Audit ----------

    public async Task AddAuditAsync(
        string actor,
        string action,
        string target,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit(ts, actor, action, target, detail)
            VALUES($ts, $actor, $action, $target, $detail);
            """;
        command.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, actor, action, target, detail FROM audit
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AuditEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                Actor = reader.GetString(2),
                Action = reader.GetString(3),
                Target = reader.GetString(4),
                Detail = reader.GetString(5),
            });
        }

        return results;
    }

    // ---------- Maestro checklist ----------

    public async Task SaveChecklistAsync(MaestroChecklist checklist, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maestro(job_id, checklist_json, updated_at)
            VALUES($id, $json, $ts)
            ON CONFLICT(job_id) DO UPDATE SET
                checklist_json = excluded.checklist_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", checklist.JobId);
        command.Parameters.AddWithValue("$json", UpdateAgentJson.Serialize(checklist));
        command.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MaestroChecklist?> GetChecklistAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checklist_json FROM maestro WHERE job_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", jobId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : UpdateAgentJson.Deserialize<MaestroChecklist>(json);
    }

    // ---------- Settings ----------

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

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>PC별 정책처럼 접두어로 묶인 설정을 한 번에 읽는다.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE key LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", $"{prefix}%");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
