using System.Text.Json;
using Microsoft.Data.Sqlite;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Observability;
using UpdateAgent.LabManager.Services;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Storage;

public sealed record AgentRecord
{
    public required AgentInfo Agent { get; init; }
    public required AgentTelemetry Telemetry { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>교사 대시보드가 표시하는 무결성/이탈 판정. 계산하지 않은 경로에서는 null이다.</summary>
    public AgentIntegrityView? Integrity { get; init; }
}

public sealed record LabJobRecord
{
    public required JobSummary Summary { get; init; }
    public JobDetail? Detail { get; init; }
    public string MachineName { get; init; } = "";

    /// <summary>서버가 발급한 상관관계 ID. 이전 작업은 null이며 화면에는 "없음"으로 표시한다.</summary>
    public string? CorrelationId { get; init; }
}

public sealed record AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Actor { get; init; } = "";
    public string Action { get; init; } = "";
    public string Target { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? CorrelationId { get; init; }
}

/// <summary>작업 상태/명령 전이 한 건. DedupeKey가 같으면 한 번만 저장된다.</summary>
public sealed record JobTransition
{
    public long Id { get; init; }
    public string JobId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public string Actor { get; init; } = "";
    public string? CorrelationId { get; init; }
    public string DedupeKey { get; init; } = "";
}

/// <summary>리포트용 전이 요약. 기록이 없으면 Count=0, 시각=null이다.</summary>
public sealed record JobTransitionSummary(
    int Count,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt);

/// <summary>리포트용 작업 행. 값이 없으면 빈 문자열 대신 null로 둔다.</summary>
public sealed record JobReportRow(
    string JobId,
    string? AgentId,
    string? MachineName,
    string State,
    string? Phase,
    string? Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? UpdatedAt,
    string? CorrelationId);

/// <summary>
/// 배치별 예약 상태. 작업 상세 JSON과 분리해 에이전트 체크인이 덮어쓰지 않도록 보존한다.
/// </summary>
public sealed record JobRolloutSchedule
{
    public required string JobId { get; init; }
    public required string AgentId { get; init; }
    public string MachineName { get; init; } = "";
    public int BatchIndex { get; init; }
    public int BatchCount { get; init; }
    public int IntervalSeconds { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public DateTimeOffset? DispatchedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
}

/// <summary>교사 PC의 LabManager SQLite 저장소. 작업 상태의 원본이다.</summary>
public sealed class LabStore
{
    /// <summary>
    /// 실시간 스냅샷과 단일 에이전트가 만들 수 있는 비종료 작업의 상한.
    /// 상한을 넘으면 최신 작업만 표시하고 나머지는 "확인 불가"로 처리한다.
    /// </summary>
    public const int MaxActiveJobs = 500;

    private readonly int _onlineThresholdSeconds;
    private readonly string _connectionString;

    // JSON으로 저장되는 모델의 시각도 UTC로 맞춘다. TEXT 컬럼과 비교 시 오프셋이 섞이지 않게 한다.
    private static AgentInfo ToUtc(AgentInfo agent) => agent with
    {
        FirstSeen = agent.FirstSeen.ToUniversalTime(),
        LastSeen = agent.LastSeen.ToUniversalTime(),
    };

    private static AgentTelemetry ToUtc(AgentTelemetry telemetry) => telemetry with
    {
        Timestamp = telemetry.Timestamp.ToUniversalTime(),
        CurrentJob = telemetry.CurrentJob is { } job ? ToUtc(job) : null,
        CurrentJobDetail = telemetry.CurrentJobDetail is { } detail ? ToUtc(detail) : null,
    };

    private static JobSummary ToUtc(JobSummary summary) => summary with
    {
        StartedAt = summary.StartedAt.ToUniversalTime(),
        UpdatedAt = summary.UpdatedAt.ToUniversalTime(),
        ReportedAt = summary.ReportedAt?.ToUniversalTime(),
        CompletedAt = summary.CompletedAt?.ToUniversalTime(),
        PauseUntil = summary.PauseUntil?.ToUniversalTime(),
    };

    private static JobDetail ToUtc(JobDetail detail) => detail with { Summary = ToUtc(detail.Summary) };

    private static MaestroChecklist ToUtc(MaestroChecklist checklist) => checklist with
    {
        ProtectionSuspendedConfirmedAt = checklist.ProtectionSuspendedConfirmedAt?.ToUniversalTime(),
        ProtectionRestoredConfirmedAt = checklist.ProtectionRestoredConfirmedAt?.ToUniversalTime(),
        MarkerVerifiedAt = checklist.MarkerVerifiedAt?.ToUniversalTime(),
    };

    public LabStore(string databasePath, int onlineThresholdSeconds = 90)
    {
        if (onlineThresholdSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(onlineThresholdSeconds));
        _onlineThresholdSeconds = onlineThresholdSeconds;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
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

            -- 기존 ix_audit_ts는 PK인 id를 다시 인덱싱했다. 시간순 조회용 인덱스를 별도로 추가한다.
            CREATE INDEX IF NOT EXISTS ix_audit_ts_v2 ON audit(ts DESC);

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

            -- 단계적 롤아웃 예약 상태. dispatched_at/cancelled_at이 null인 행만 발송 대상이다.
            CREATE TABLE IF NOT EXISTS job_rollout (
                job_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                batch_index INTEGER NOT NULL,
                batch_count INTEGER NOT NULL,
                interval_seconds INTEGER NOT NULL,
                due_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                dispatched_at TEXT,
                cancelled_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_job_rollout_due
                ON job_rollout(dispatched_at, cancelled_at, due_at);

            -- 작업 타임라인. dedupe_key로 같은 전이를 여러 번 기록하지 않는다.
            CREATE TABLE IF NOT EXISTS job_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                ts TEXT NOT NULL,
                event TEXT NOT NULL,
                from_state TEXT,
                to_state TEXT,
                phase TEXT NOT NULL,
                message TEXT NOT NULL,
                actor TEXT NOT NULL,
                correlation_id TEXT,
                dedupe_key TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_job_transitions_job
                ON job_transitions(job_id, ts, id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_job_transitions_dedupe
                ON job_transitions(job_id, dedupe_key);

            -- 이전 릴리스에서 대소문자만 다른 에이전트/정책 키가 저장될 수 있었다.
            -- 최신 행 하나만 남기고 모두 소문자로 정규화해 중복 PC와 정책 키 충돌을 제거한다.
            WITH ranked AS (
                SELECT agent_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY lower(agent_id)
                           ORDER BY last_seen DESC, agent_id) AS rn
                FROM agents
            )
            DELETE FROM agents WHERE agent_id IN (SELECT agent_id FROM ranked WHERE rn > 1);
            UPDATE agents SET agent_id = lower(agent_id) WHERE agent_id <> lower(agent_id);

            WITH ranked AS (
                SELECT key,
                       ROW_NUMBER() OVER (
                           PARTITION BY lower(key)
                           ORDER BY updated_at DESC, key) AS rn
                FROM settings WHERE key LIKE 'agent-policy:%'
            )
            DELETE FROM settings WHERE key IN (SELECT key FROM ranked WHERE rn > 1);
            UPDATE settings SET key = lower(key)
                WHERE key LIKE 'agent-policy:%' AND key <> lower(key);

            UPDATE jobs SET agent_id = lower(agent_id) WHERE agent_id <> lower(agent_id);
            UPDATE commands SET agent_id = lower(agent_id) WHERE agent_id <> lower(agent_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 기존 DB도 상관관계 ID 컬럼을 갖도록 한다. 값이 없는 이전 작업은 null로 남는다.
        await EnsureColumnAsync(connection, "jobs", "correlation_id", cancellationToken);
        await EnsureColumnAsync(connection, "audit", "correlation_id", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------- Agents ----------

    public async Task UpsertAgentAsync(
        AgentInfo agent,
        AgentTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        // 대소문자만 다른 hostname 재등록은 별도 PC가 아니라 같은 PC로 저장한다.
        var canonicalId = AgentIdGuard.Canonical(agent.AgentId);
        agent = agent with { AgentId = canonicalId };
        telemetry = telemetry with { AgentId = canonicalId };

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
        command.Parameters.AddWithValue("$id", canonicalId);
        command.Parameters.AddWithValue("$name", agent.MachineName);
        command.Parameters.AddWithValue("$state", (int)telemetry.State);
        command.Parameters.AddWithValue("$seen", UtcTimestamps.FormatNow());
        command.Parameters.AddWithValue("$info", UpdateAgentJson.Serialize(ToUtc(agent)));
        // 사용자 표시 이름은 개인정보이므로 DB에 저장하지 않는다. 레거시 값도 여기서 제거한다.
        command.Parameters.AddWithValue("$telemetry",
            UpdateAgentJson.Serialize(ToUtc(telemetry) with { LoggedOnUsers = [] }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>재이미징/폐기된 PC를 목록에서 제거한다. 작업 이력은 보존한다.</summary>
    public async Task<bool> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var canonicalId = AgentIdGuard.Canonical(agentId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agents WHERE agent_id = $id;";
        command.Parameters.AddWithValue("$id", canonicalId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;

        await using var settingCommand = connection.CreateCommand();
        settingCommand.CommandText = "DELETE FROM settings WHERE key = $key;";
        settingCommand.Parameters.AddWithValue("$key", "agent-policy:" + canonicalId);
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
            info = ToUtc(info);
            telemetry = ToUtc(telemetry) with { LoggedOnUsers = [] };

            var lastSeen = DateTimeOffset.TryParse(reader.GetString(2), out var parsed)
                ? parsed.ToUniversalTime()
                : info.LastSeen;

            results.Add(new AgentRecord
            {
                Agent = info,
                Telemetry = telemetry,
                LastSeen = lastSeen,
                Online = UtcTimestamps.Now - lastSeen < TimeSpan.FromSeconds(_onlineThresholdSeconds),
            });
        }

        return results;
    }

    public async Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var canonicalId = AgentIdGuard.Canonical(agentId);
        var agents = await GetAgentsAsync(cancellationToken);
        return agents.FirstOrDefault(a =>
            string.Equals(a.Agent.AgentId, canonicalId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> CountAgentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agents;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    // ---------- Jobs ----------

    public async Task SaveJobAsync(
        JobDetail detail,
        string machineName,
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        var canonicalId = AgentIdGuard.Canonical(detail.Summary.AgentId);
        var summary = ToUtc(detail.Summary with { AgentId = canonicalId });
        detail = ToUtc(detail with { Summary = detail.Summary with { AgentId = canonicalId } });
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, agent_id, machine_name, state, phase, progress,
                             created_at, updated_at, summary_json, detail_json, correlation_id)
            VALUES($id, $agent, $machine, $state, $phase, $progress, $created, $updated, $summary, $detail, $correlation)
            ON CONFLICT(job_id) DO UPDATE SET
                machine_name = excluded.machine_name,
                state = excluded.state,
                phase = excluded.phase,
                progress = excluded.progress,
                updated_at = excluded.updated_at,
                summary_json = excluded.summary_json,
                detail_json = COALESCE(excluded.detail_json, jobs.detail_json),
                correlation_id = COALESCE(excluded.correlation_id, jobs.correlation_id);
            """;
        command.Parameters.AddWithValue("$id", summary.JobId);
        command.Parameters.AddWithValue("$agent", summary.AgentId);
        command.Parameters.AddWithValue("$machine", machineName);
        command.Parameters.AddWithValue("$state", (int)summary.State);
        command.Parameters.AddWithValue("$phase", summary.Phase);
        command.Parameters.AddWithValue("$progress", summary.ProgressPercent);
        command.Parameters.AddWithValue("$created", UtcTimestamps.Format(summary.StartedAt));
        command.Parameters.AddWithValue("$updated", UtcTimestamps.Format(summary.UpdatedAt));
        command.Parameters.AddWithValue("$summary", UpdateAgentJson.Serialize(summary));
        command.Parameters.AddWithValue("$detail", UpdateAgentJson.Serialize(detail));
        command.Parameters.AddWithValue("$correlation", string.IsNullOrWhiteSpace(correlationId) ? DBNull.Value : correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveJobSummaryAsync(
        JobSummary summary,
        string machineName,
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        summary = ToUtc(summary with { AgentId = AgentIdGuard.Canonical(summary.AgentId) });
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, agent_id, machine_name, state, phase, progress,
                             created_at, updated_at, summary_json, detail_json, correlation_id)
            VALUES($id, $agent, $machine, $state, $phase, $progress, $created, $updated, $summary, NULL, $correlation)
            ON CONFLICT(job_id) DO UPDATE SET
                machine_name = excluded.machine_name,
                state = excluded.state,
                phase = excluded.phase,
                progress = excluded.progress,
                updated_at = excluded.updated_at,
                summary_json = excluded.summary_json,
                correlation_id = COALESCE(excluded.correlation_id, jobs.correlation_id);
            """;
        command.Parameters.AddWithValue("$id", summary.JobId);
        command.Parameters.AddWithValue("$agent", summary.AgentId);
        command.Parameters.AddWithValue("$machine", machineName);
        command.Parameters.AddWithValue("$state", (int)summary.State);
        command.Parameters.AddWithValue("$phase", summary.Phase);
        command.Parameters.AddWithValue("$progress", summary.ProgressPercent);
        command.Parameters.AddWithValue("$created", UtcTimestamps.Format(summary.StartedAt));
        command.Parameters.AddWithValue("$updated", UtcTimestamps.Format(summary.UpdatedAt));
        command.Parameters.AddWithValue("$summary", UpdateAgentJson.Serialize(summary));
        command.Parameters.AddWithValue("$correlation", string.IsNullOrWhiteSpace(correlationId) ? DBNull.Value : correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LabJobRecord>> GetJobsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json, detail_json, machine_name, correlation_id FROM jobs
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<LabJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
            if (summary is null) continue;
            summary = ToUtc(summary);

            var detail = reader.IsDBNull(1)
                ? null
                : UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(1));
            if (detail is not null) detail = ToUtc(detail);

            results.Add(new LabJobRecord
            {
                Summary = summary,
                Detail = detail,
                MachineName = reader.GetString(2),
                CorrelationId = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return results;
    }

    /// <summary>
    /// 실시간 모니터에 보여줄 활성 작업을 최신순 상한까지만 반환한다.
    /// 전체 개수는 CountActiveJobsAsync가 별도로 제공하므로 누락된 작업은 "확인 불가"로 표시할 수 있다.
    /// </summary>
    public async Task<IReadOnlyList<LabJobRecord>> GetActiveJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json, detail_json, machine_name, correlation_id FROM jobs
            WHERE state NOT IN ($completed, $failed, $cancelled)
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$completed", (int)UpdateJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)UpdateJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)UpdateJobState.Cancelled);
        command.Parameters.AddWithValue("$limit", MaxActiveJobs);

        var results = new List<LabJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
            if (summary is null) continue;
            summary = ToUtc(summary);

            var detail = reader.IsDBNull(1)
                ? null
                : UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(1));
            if (detail is not null) detail = ToUtc(detail);

            results.Add(new LabJobRecord
            {
                Summary = summary,
                Detail = detail,
                MachineName = reader.GetString(2),
                CorrelationId = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return results;
    }

    public async Task<LabJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json, detail_json, machine_name, correlation_id FROM jobs
            WHERE job_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
        if (summary is null) return null;
        summary = ToUtc(summary);

        var detail = reader.IsDBNull(1)
            ? null
            : UpdateAgentJson.Deserialize<JobDetail>(reader.GetString(1));
        if (detail is not null) detail = ToUtc(detail);

        return new LabJobRecord
        {
            Summary = summary,
            Detail = detail,
            MachineName = reader.GetString(2),
            CorrelationId = reader.IsDBNull(3) ? null : reader.GetString(3),
        };
    }

    /// <summary>
    /// 이 에이전트에 새 비종료 작업을 배정할 수 있는지. 체크인 경로와 교사 작업 배정
    /// 경로가 같은 상한을 사용하도록 공유 판정으로 둔다.
    /// </summary>
    public async Task<bool> CanAcceptNewJobAsync(
        string agentId,
        CancellationToken cancellationToken = default) =>
        await CountActiveJobsAsync(agentId, cancellationToken) < MaxActiveJobs;

    /// <summary>단일 에이전트가 현재 남긴 비종료 작업 수. 새 작업 상한을 강제할 때 사용한다.</summary>
    public async Task<int> CountActiveJobsAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM jobs
            WHERE agent_id = $agent COLLATE NOCASE
              AND state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$agent", AgentIdGuard.Canonical(agentId));
        command.Parameters.AddWithValue("$completed", (int)UpdateJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)UpdateJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)UpdateJobState.Cancelled);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
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

    /// <summary>
    /// 완료/실패 작업 수를 전체 테이블에서 센다. 최근 N건만 읽는 GetJobsAsync로 세면
    /// 오래된 랩에서 실제보다 적게 표시된다.
    /// </summary>
    public async Task<(int Completed, int Failed)> CountFinishedJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN state = $completed THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = $failed THEN 1 ELSE 0 END)
            FROM jobs;
            """;
        command.Parameters.AddWithValue("$completed", (int)UpdateJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)UpdateJobState.Failed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    /// <summary>
    /// Maestro 확인이 필요한 활성 작업 수를 전체 테이블에서 센다.
    /// 최근 N건으로 세면 오래 정지된 작업이 목록에서 밀려 0으로 표시된다.
    /// </summary>
    public async Task<int> CountAttentionJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_json FROM jobs
            WHERE state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$completed", (int)UpdateJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)UpdateJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)UpdateJobState.Cancelled);

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = UpdateAgentJson.Deserialize<JobSummary>(reader.GetString(0));
            if (summary is not null &&
                summary.RequiresMaestroAttention &&
                JobStateRules.IsActive(summary.State.ToString()))
            {
                count++;
            }
        }
        return count;
    }

    // ---------- Commands ----------

    public async Task AddCommandAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        command = command with { AgentId = AgentIdGuard.Canonical(command.AgentId) };
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
        command2.Parameters.AddWithValue("$created", UtcTimestamps.Format(command.CreatedAt));
        command2.Parameters.AddWithValue("$expires", UtcTimestamps.Format(command.ExpiresAt));
        await command2.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCommand>> TakePendingCommandsAsync(
        string agentId,
        int max,
        CancellationToken cancellationToken = default)
    {
        var now = UtcTimestamps.FormatNow();
        var results = new List<AgentCommand>();

        await using var connection = await OpenAsync(cancellationToken);
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT command_id, agent_id, type, status, payload_json, issued_by,
                       created_at, expires_at
                FROM commands
                WHERE agent_id = $agent COLLATE NOCASE
                  AND status IN ($pending, $delivered)
                  AND expires_at > $now
                ORDER BY created_at LIMIT $max;
                """;
            select.Parameters.AddWithValue("$agent", AgentIdGuard.Canonical(agentId));
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
                    CreatedAt = UtcTimestamps.Parse(reader.GetString(6)),
                    ExpiresAt = UtcTimestamps.Parse(reader.GetString(7)),
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

    /// <summary>실제로 상태가 바뀐(유효한) 명령 ID 집합을 반환한다.</summary>
    public async Task<IReadOnlySet<string>> CompleteCommandResultsAsync(
        string agentId,
        IEnumerable<CommandResult> results,
        CancellationToken cancellationToken = default)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 공유 에이전트 토큰을 가진 다른 PC의 결과, 이미 종료된 명령, 만료된 명령은 거부한다.
        command.CommandText = """
            UPDATE commands SET status = $status, result_message = $message, completed_at = $ts
            WHERE command_id = $id AND agent_id = $agent COLLATE NOCASE
              AND status IN ($pending, $delivered)
              AND expires_at > $now;
            """;
        var statusParam = command.Parameters.Add("$status", SqliteType.Integer);
        var messageParam = command.Parameters.Add("$message", SqliteType.Text);
        var tsParam = command.Parameters.Add("$ts", SqliteType.Text);
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var agentParam = command.Parameters.Add("$agent", SqliteType.Text);
        var pendingParam = command.Parameters.Add("$pending", SqliteType.Integer);
        var deliveredParam = command.Parameters.Add("$delivered", SqliteType.Integer);
        var nowParam = command.Parameters.Add("$now", SqliteType.Text);
        agentParam.Value = AgentIdGuard.Canonical(agentId);
        pendingParam.Value = (int)CommandStatus.Pending;
        deliveredParam.Value = (int)CommandStatus.Delivered;
        // 만료 비교와 완료 시각 모두 서버 UTC 시계만 사용한다. 에이전트 보고 시각은 신뢰하지 않는다.
        var serverNow = UtcTimestamps.FormatNow();
        nowParam.Value = serverNow;
        tsParam.Value = serverNow;

        foreach (var result in results)
        {
            statusParam.Value = (int)(result.Success ? CommandStatus.Succeeded : CommandStatus.Failed);
            messageParam.Value = result.Message;
            idParam.Value = result.CommandId;
            if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                accepted.Add(result.CommandId);
            }
        }

        return accepted;
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
            CreatedAt = UtcTimestamps.Parse(reader.GetString(6)),
            ExpiresAt = UtcTimestamps.Parse(reader.GetString(7)),
            ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
            CompletedAt = reader.IsDBNull(9) ? null : UtcTimestamps.Parse(reader.GetString(9)),
        };
    }

    public async Task<IReadOnlyList<AgentCommand>> GetCommandsAsync(
        string? agentId = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = agentId is null
            ? """
              SELECT command_id, agent_id, type, status, payload_json, issued_by,
                     created_at, expires_at, result_message, completed_at
              FROM commands ORDER BY created_at DESC LIMIT $limit;
              """
            : """
              SELECT command_id, agent_id, type, status, payload_json, issued_by,
                     created_at, expires_at, result_message, completed_at
              FROM commands WHERE agent_id = $agent COLLATE NOCASE ORDER BY created_at DESC LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$limit", limit);
        if (agentId is not null) command.Parameters.AddWithValue("$agent", AgentIdGuard.Canonical(agentId));

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
                CreatedAt = UtcTimestamps.Parse(reader.GetString(6)),
                ExpiresAt = UtcTimestamps.Parse(reader.GetString(7)),
                ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                CompletedAt = reader.IsDBNull(9) ? null : UtcTimestamps.Parse(reader.GetString(9)),
            });
        }

        return results;
    }

    // ---------- 단계적 롤아웃 ----------

    /// <summary>
    /// 배치 예약을 저장한다. 첫 배치의 명령/감사까지 같은 트랜잭션에 넣어
    /// 재시작 후 같은 작업이 두 번 발급되지 않게 한다. 이미 저장된 작업은 무시한다.
    /// </summary>
    public async Task<bool> SaveRolloutScheduleAsync(
        JobRolloutSchedule schedule,
        AgentCommand? firstCommand,
        CancellationToken cancellationToken = default)
    {
        var agentId = AgentIdGuard.Canonical(schedule.AgentId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO job_rollout(job_id, agent_id, machine_name, batch_index,
                                              batch_count, interval_seconds, due_at,
                                              payload_json, dispatched_at, cancelled_at)
            VALUES($id, $agent, $machine, $batch, $count, $interval, $due, $payload, $dispatched, NULL);
            """;
        insert.Parameters.AddWithValue("$id", schedule.JobId);
        insert.Parameters.AddWithValue("$agent", agentId);
        insert.Parameters.AddWithValue("$machine", schedule.MachineName);
        insert.Parameters.AddWithValue("$batch", schedule.BatchIndex);
        insert.Parameters.AddWithValue("$count", schedule.BatchCount);
        insert.Parameters.AddWithValue("$interval", schedule.IntervalSeconds);
        insert.Parameters.AddWithValue("$due", UtcTimestamps.Format(schedule.DueAt));
        insert.Parameters.AddWithValue("$payload", schedule.PayloadJson);
        // Microsoft.Data.Sqlite는 null 값에 DBNull.Value를 요구한다(C# null은 바인딩 오류).
        insert.Parameters.AddWithValue(
            "$dispatched",
            firstCommand is null ? DBNull.Value : UtcTimestamps.Format(firstCommand.CreatedAt));

        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        if (firstCommand is not null)
        {
            await using var commandInsert = connection.CreateCommand();
            commandInsert.Transaction = transaction;
            commandInsert.CommandText = """
                INSERT INTO commands(command_id, agent_id, type, status, payload_json,
                                     issued_by, created_at, expires_at)
                VALUES($id, $agent, $type, $status, $payload, $issued, $created, $expires);
                """;
            commandInsert.Parameters.AddWithValue("$id", firstCommand.CommandId);
            commandInsert.Parameters.AddWithValue("$agent", AgentIdGuard.Canonical(firstCommand.AgentId));
            commandInsert.Parameters.AddWithValue("$type", (int)firstCommand.Type);
            commandInsert.Parameters.AddWithValue("$status", (int)firstCommand.Status);
            commandInsert.Parameters.AddWithValue("$payload", firstCommand.PayloadJson);
            commandInsert.Parameters.AddWithValue("$issued", firstCommand.IssuedBy);
            commandInsert.Parameters.AddWithValue("$created", UtcTimestamps.Format(firstCommand.CreatedAt));
            commandInsert.Parameters.AddWithValue("$expires", UtcTimestamps.Format(firstCommand.ExpiresAt));
            await commandInsert.ExecuteNonQueryAsync(cancellationToken);
            // 첫 배치의 감사는 호출부가 기존과 같이 teacher/job-start로 남긴다.
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<JobRolloutSchedule?> GetRolloutScheduleAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, machine_name, batch_index, batch_count, interval_seconds,
                   due_at, payload_json, dispatched_at, cancelled_at
            FROM job_rollout WHERE job_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new JobRolloutSchedule
        {
            JobId = jobId,
            AgentId = reader.GetString(0),
            MachineName = reader.GetString(1),
            BatchIndex = reader.GetInt32(2),
            BatchCount = reader.GetInt32(3),
            IntervalSeconds = reader.GetInt32(4),
            DueAt = UtcTimestamps.Parse(reader.GetString(5)),
            PayloadJson = reader.GetString(6),
            DispatchedAt = reader.IsDBNull(7) ? null : UtcTimestamps.Parse(reader.GetString(7)),
            CancelledAt = reader.IsDBNull(8) ? null : UtcTimestamps.Parse(reader.GetString(8)),
        };
    }

    /// <summary>
    /// 지금 전송해야 하는 예약 배치를 원자적으로 명령화한다. 명령 삽입과 dispatched_at 갱신이
    /// 같은 트랜잭션이라 중복 틱/재시작/재시도에서도 StartJob이 두 번 생기지 않는다.
    /// </summary>
    public async Task<IReadOnlyList<string>> DispatchDueRolloutJobsAsync(
        DateTimeOffset? now = null,
        int maxJobs = 100,
        CancellationToken cancellationToken = default)
    {
        if (maxJobs <= 0) throw new ArgumentOutOfRangeException(nameof(maxJobs));

        var reference = (now ?? UtcTimestamps.Now).ToUniversalTime();
        var stamp = UtcTimestamps.Format(reference);
        var dispatched = new List<string>();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var rows = new List<(string JobId, string AgentId, string MachineName, int Batch, int Count, string Payload, int? JobState)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT r.job_id, r.agent_id, r.machine_name, r.batch_index, r.batch_count,
                       r.payload_json, j.state
                FROM job_rollout r
                LEFT JOIN jobs j ON j.job_id = r.job_id
                WHERE r.dispatched_at IS NULL AND r.cancelled_at IS NULL AND r.due_at <= $now
                ORDER BY r.due_at, r.batch_index, r.job_id
                LIMIT $max;
                """;
            select.Parameters.AddWithValue("$now", stamp);
            select.Parameters.AddWithValue("$max", maxJobs);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }
        }

        foreach (var row in rows)
        {
            if (row.JobState is null ||
                row.JobState == (int)UpdateJobState.Cancelled ||
                row.JobState == (int)UpdateJobState.Completed ||
                row.JobState == (int)UpdateJobState.Failed)
            {
                // 취소/종료된 작업은 예약만 정리하고 명령을 만들지 않는다.
                await using var skip = connection.CreateCommand();
                skip.Transaction = transaction;
                skip.CommandText = """
                    UPDATE job_rollout SET cancelled_at = $stamp
                    WHERE job_id = $id AND dispatched_at IS NULL AND cancelled_at IS NULL;
                    """;
                skip.Parameters.AddWithValue("$stamp", stamp);
                skip.Parameters.AddWithValue("$id", row.JobId);
                await skip.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            var commandId = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO commands(command_id, agent_id, type, status, payload_json,
                                     issued_by, created_at, expires_at)
                VALUES($id, $agent, $type, $status, $payload, $issued, $created, $expires);
                """;
            insert.Parameters.AddWithValue("$id", commandId);
            insert.Parameters.AddWithValue("$agent", AgentIdGuard.Canonical(row.AgentId));
            insert.Parameters.AddWithValue("$type", (int)CommandType.StartJob);
            insert.Parameters.AddWithValue("$status", (int)CommandStatus.Pending);
            insert.Parameters.AddWithValue("$payload", row.Payload);
            insert.Parameters.AddWithValue("$issued", "system");
            insert.Parameters.AddWithValue("$created", stamp);
            insert.Parameters.AddWithValue("$expires", UtcTimestamps.Format(reference.AddHours(12)));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit(ts, actor, action, target, detail, correlation_id)
                VALUES($ts, $actor, $action, $target, $detail, $correlation);
                """;
            audit.Parameters.AddWithValue("$ts", stamp);
            audit.Parameters.AddWithValue("$actor", "system");
            audit.Parameters.AddWithValue("$action", "rollout-dispatch");
            audit.Parameters.AddWithValue("$target", $"{row.MachineName}/{row.JobId}");
            audit.Parameters.AddWithValue("$detail", $"배치 {row.Batch}/{row.Count} 시작 명령 발급");
            audit.Parameters.AddWithValue(
                "$correlation",
                ReadStringProperty(row.Payload, "correlationId") is { } dispatchCorrelation
                    ? (object)dispatchCorrelation
                    : DBNull.Value);
            await audit.ExecuteNonQueryAsync(cancellationToken);

            await AddJobTransitionAsync(new JobTransition
            {
                JobId = row.JobId,
                Timestamp = reference,
                Event = "rollout-dispatch",
                FromState = row.JobState is { } state && Enum.IsDefined(typeof(UpdateJobState), state)
                    ? ((UpdateJobState)state).ToString()
                    : null,
                Phase = "명령 전달",
                Message = $"배치 {row.Batch}/{row.Count} 시작 명령 발급",
                Actor = "system",
                CorrelationId = ReadStringProperty(row.Payload, "correlationId"),
                DedupeKey = $"rollout:{row.JobId}:{row.Batch}:{commandId}",
            }, connection, transaction, cancellationToken);

            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE job_rollout SET dispatched_at = $stamp
                WHERE job_id = $id AND dispatched_at IS NULL AND cancelled_at IS NULL;
                """;
            mark.Parameters.AddWithValue("$stamp", stamp);
            mark.Parameters.AddWithValue("$id", row.JobId);
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("예약 배치 상태를 갱신하지 못했습니다.");
            }

            dispatched.Add(row.JobId);
        }

        await transaction.CommitAsync(cancellationToken);
        return dispatched;
    }

    /// <summary>아직 발송되지 않은 예약을 취소한다. 발송된 배치는 그대로 둔다.</summary>
    public async Task<bool> CancelRolloutAsync(
        string jobId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var stamp = UtcTimestamps.Format((now ?? UtcTimestamps.Now).ToUniversalTime());
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE job_rollout SET cancelled_at = $stamp
            WHERE job_id = $id AND dispatched_at IS NULL AND cancelled_at IS NULL;
            """;
        command.Parameters.AddWithValue("$stamp", stamp);
        command.Parameters.AddWithValue("$id", jobId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // ---------- Audit ----------

    public async Task AddAuditAsync(
        string actor,
        string action,
        string target,
        string detail,
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit(ts, actor, action, target, detail, correlation_id)
            VALUES($ts, $actor, $action, $target, $detail, $correlation);
            """;
        command.Parameters.AddWithValue("$ts", UtcTimestamps.FormatNow());
        command.Parameters.AddWithValue("$actor", TextGuard.Truncate(actor, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue("$action", TextGuard.Truncate(action, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue("$target", TextGuard.Truncate(target, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$detail",
            TextGuard.Truncate(ObservabilityEvent.MaskSecrets(detail) ?? "", TextGuard.MaxAuditTextLength));
        command.Parameters.AddWithValue(
            "$correlation",
            ObservabilityEvent.MaskSecrets(correlationId) is { } safeCorrelation
                ? TextGuard.Truncate(safeCorrelation, TextGuard.MaxLabelLength)
                : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, actor, action, target, detail, correlation_id FROM audit
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
                Timestamp = UtcTimestamps.Parse(reader.GetString(1)),
                Actor = reader.GetString(2),
                Action = reader.GetString(3),
                Target = reader.GetString(4),
                Detail = reader.GetString(5),
                CorrelationId = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return results;
    }

    // ---------- 작업 전이 타임라인 ----------

    private const string TransitionInsertSql = """
        INSERT OR IGNORE INTO job_transitions(job_id, ts, event, from_state, to_state,
                                              phase, message, actor, correlation_id, dedupe_key)
        VALUES($job, $ts, $event, $from, $to, $phase, $message, $actor, $correlation, $dedupe);
        """;

    /// <summary>중복 키가 이미 있으면 false를 돌려준다. 타임라인은 한 번만 기록한다.</summary>
    public async Task<bool> AddJobTransitionAsync(
        JobTransition transition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = TransitionInsertSql;
        BindTransition(command, transition);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>같은 트랜잭션 안에서 전이를 기록할 때 사용한다(예약 배치 발급).</summary>
    private static async Task<bool> AddJobTransitionAsync(
        JobTransition transition,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TransitionInsertSql;
        BindTransition(command, transition);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string SafeTransitionText(string? value, int maxLength) =>
        TextGuard.Truncate(ObservabilityEvent.MaskSecrets(value) ?? "", maxLength);

    private static object SafeTransitionValue(string? value, int maxLength) =>
        ObservabilityEvent.MaskSecrets(value) is { } safe
            ? TextGuard.Truncate(safe, maxLength)
            : DBNull.Value;

    private static void BindTransition(SqliteCommand command, JobTransition transition)
    {
        var timestamp = transition.Timestamp == default ? UtcTimestamps.Now : transition.Timestamp;
        command.Parameters.AddWithValue(
            "$job", SafeTransitionText(transition.JobId, TextGuard.MaxIdentifierLength));
        command.Parameters.AddWithValue("$ts", UtcTimestamps.Format(timestamp));
        command.Parameters.AddWithValue(
            "$event", SafeTransitionText(transition.Event, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$from", SafeTransitionValue(transition.FromState, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$to", SafeTransitionValue(transition.ToState, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$phase", SafeTransitionText(transition.Phase, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$message", SafeTransitionText(transition.Message, TextGuard.MaxAuditTextLength));
        command.Parameters.AddWithValue(
            "$actor", SafeTransitionText(transition.Actor, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$correlation", SafeTransitionValue(transition.CorrelationId, TextGuard.MaxLabelLength));
        command.Parameters.AddWithValue(
            "$dedupe", string.IsNullOrWhiteSpace(transition.DedupeKey)
                ? Guid.NewGuid().ToString("N")
                : SafeTransitionText(transition.DedupeKey, TextGuard.MaxAuditTextLength));
    }

    public async Task<IReadOnlyList<JobTransition>> GetTransitionsAsync(
        string jobId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, ts, event, from_state, to_state, phase, message, actor, correlation_id
            FROM job_transitions WHERE job_id = $id
            ORDER BY ts ASC, id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var results = new List<JobTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new JobTransition
            {
                Id = reader.GetInt64(0),
                JobId = reader.GetString(1),
                Timestamp = UtcTimestamps.Parse(reader.GetString(2)),
                Event = SafeTransitionText(reader.GetString(3), TextGuard.MaxLabelLength),
                FromState = reader.IsDBNull(4)
                    ? null
                    : ObservabilityEvent.MaskSecrets(reader.GetString(4)),
                ToState = reader.IsDBNull(5)
                    ? null
                    : ObservabilityEvent.MaskSecrets(reader.GetString(5)),
                Phase = SafeTransitionText(reader.GetString(6), TextGuard.MaxLabelLength),
                Message = SafeTransitionText(reader.GetString(7), TextGuard.MaxAuditTextLength),
                Actor = SafeTransitionText(reader.GetString(8), TextGuard.MaxLabelLength),
                CorrelationId = reader.IsDBNull(9)
                    ? null
                    : ObservabilityEvent.MaskSecrets(reader.GetString(9)),
            });
        }
        return results;
    }

    // ---------- 작업 리포트 ----------

    /// <summary>
    /// 기간 내 전체 건수와 상한 내 행을 함께 읽는다. 잘린 목록을 전체로 오인하지 않도록
    /// 호출부가 totalCount와 returnedCount를 비교해 생략 건수를 계산한다.
    /// </summary>
    public async Task<(int TotalCount, IReadOnlyList<JobReportRow> Jobs, IReadOnlyDictionary<string, JobTransitionSummary> Transitions)> GetJobReportAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var fromText = UtcTimestamps.Format(from ?? DateTimeOffset.MinValue);
        var toText = UtcTimestamps.Format(to ?? DateTimeOffset.MaxValue);
        await using var connection = await OpenAsync(cancellationToken);

        int totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = """
                SELECT COUNT(*) FROM jobs WHERE created_at >= $from AND created_at <= $to;
                """;
            count.Parameters.AddWithValue("$from", fromText);
            count.Parameters.AddWithValue("$to", toText);
            totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        var rows = new List<JobReportRow>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT job_id, agent_id, machine_name, state, phase, created_at, updated_at,
                       correlation_id, summary_json
                FROM jobs
                WHERE created_at >= $from AND created_at <= $to
                ORDER BY created_at DESC, job_id DESC LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$from", fromText);
            select.Parameters.AddWithValue("$to", toText);
            select.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stateValue = reader.GetInt32(3);
                var state = Enum.IsDefined(typeof(UpdateJobState), stateValue)
                    ? ((UpdateJobState)stateValue).ToString()
                    : "확인 불가";
                rows.Add(new JobReportRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    state,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ReadSummaryMessage(reader.IsDBNull(8) ? null : reader.GetString(8)),
                    TryParseStamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    TryParseStamp(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        var transitions = new Dictionary<string, JobTransitionSummary>(StringComparer.OrdinalIgnoreCase);
        await using (var summary = connection.CreateCommand())
        {
            summary.CommandText = """
                SELECT job_id, COUNT(*), MIN(ts), MAX(ts) FROM job_transitions GROUP BY job_id;
                """;
            await using var reader = await summary.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                transitions[reader.GetString(0)] = new JobTransitionSummary(
                    reader.GetInt32(1),
                    TryParseStamp(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    TryParseStamp(reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return (totalCount, rows, transitions);
    }

    private static string? ReadSummaryMessage(string? json) => ReadStringProperty(json, "message");

    private static string? ReadStringProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? ObservabilityEvent.MaskSecrets(value.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseStamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

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
        command.Parameters.AddWithValue("$json", UpdateAgentJson.Serialize(ToUtc(checklist)));
        command.Parameters.AddWithValue("$ts", UtcTimestamps.FormatNow());
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
        command.Parameters.AddWithValue("$ts", UtcTimestamps.FormatNow());
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

    /// <summary>
    /// 오래된 명령(7일)과 감사 로그(30일)를 정리한다. 작업/체크리스트/정책은 보존한다.
    /// 반환값은 삭제된 행 수다.
    /// </summary>
    public async Task<int> CleanupRetentionAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var reference = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM commands WHERE created_at < $commandCutoff;
            DELETE FROM audit WHERE ts < $auditCutoff;
            """;
        command.Parameters.AddWithValue("$commandCutoff", UtcTimestamps.Format(reference.AddDays(-7)));
        command.Parameters.AddWithValue("$auditCutoff", UtcTimestamps.Format(reference.AddDays(-30)));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
