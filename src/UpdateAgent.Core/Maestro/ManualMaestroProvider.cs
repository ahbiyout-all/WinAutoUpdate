using System.Text.Json;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Core.Maestro;

/// <summary>복원 프로그램 연동 인터페이스. MVP는 Maestro 수동 모드만 구현한다.</summary>
public interface IRestorationProvider
{
    string Name { get; }
    Task<MaestroDetectionResult> DetectAsync(CancellationToken cancellationToken = default);
    Task<MaestroChecklist> GetChecklistAsync(string jobId, CancellationToken cancellationToken = default);
    Task<MaestroChecklist> ConfirmProtectionSuspendedAsync(
        string jobId,
        string confirmedBy,
        CancellationToken cancellationToken = default);
    Task<MaestroChecklist> ConfirmRestoredAsync(
        string jobId,
        bool stateSaved,
        bool protectionRestored,
        CancellationToken cancellationToken = default);
    Task<RestoreMarker> WriteResumeMarkerAsync(
        string jobId,
        string agentId,
        int bootCount,
        string phase,
        CancellationToken cancellationToken = default);
    Task<MarkerVerificationResult> VerifyAfterRebootAsync(
        string jobId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maestro Network 7 수동 모드.
/// 교사의 확인을 기록하고, 재부팅 후 마커가 유지되는지 검증한다.
/// Maestro 설정/서비스는 변경하지 않는다.
/// </summary>
public sealed class ManualMaestroProvider : IRestorationProvider
{
    private readonly MaestroDetector _detector;
    private readonly string _checklistPath;
    private readonly string _markerPath;
    private readonly string _stableMarkerPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ManualMaestroProvider(string? dataDirectory = null)
    {
        _detector = new MaestroDetector();
        var dataDir = dataDirectory ?? UpdateAgentConstants.DataDirectory;
        Directory.CreateDirectory(dataDir);
        _checklistPath = Path.Combine(dataDir, "maestro-checklist.json");
        _markerPath = Path.Combine(dataDir, "resume-marker.json");

        var stableDir = UpdateAgentConstants.ResolveDataDirectory();
        Directory.CreateDirectory(stableDir);
        _stableMarkerPath = Path.Combine(stableDir, "stable-resume-marker.json");
    }

    public string Name => "Maestro Network 7 (수동 모드)";

    public Task<MaestroDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(_detector.Detect, cancellationToken);

    public async Task<MaestroChecklist> GetChecklistAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadChecklistsAsync(cancellationToken);
            return all.TryGetValue(jobId, out var checklist)
                ? checklist
                : new MaestroChecklist { JobId = jobId };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MaestroChecklist> ConfirmProtectionSuspendedAsync(
        string jobId,
        string confirmedBy,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadChecklistsAsync(cancellationToken);
            var current = all.TryGetValue(jobId, out var existing)
                ? existing
                : new MaestroChecklist { JobId = jobId };

            var updated = current with
            {
                ProtectionSuspendedConfirmed = true,
                ProtectionSuspendedConfirmedAt = UpdateAgent.Guards.UtcClock.Now,
                ConfirmedBy = confirmedBy,
            };
            all[jobId] = updated;
            await SaveChecklistsAsync(all, cancellationToken);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MaestroChecklist> ConfirmRestoredAsync(
        string jobId,
        bool stateSaved,
        bool protectionRestored,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadChecklistsAsync(cancellationToken);
            var current = all.TryGetValue(jobId, out var existing)
                ? existing
                : new MaestroChecklist { JobId = jobId };

            var updated = current with
            {
                StateSavedConfirmed = stateSaved,
                ProtectionRestoredConfirmed = protectionRestored,
                ProtectionRestoredConfirmedAt = UpdateAgent.Guards.UtcClock.Now,
            };
            all[jobId] = updated;
            await SaveChecklistsAsync(all, cancellationToken);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RestoreMarker> WriteResumeMarkerAsync(
        string jobId,
        string agentId,
        int bootCount,
        string phase,
        CancellationToken cancellationToken = default)
    {
        var marker = new RestoreMarker
        {
            JobId = jobId,
            AgentId = agentId,
            Nonce = Guid.NewGuid().ToString("N"),
            BootCount = bootCount,
            Phase = phase,
            WrittenAt = UpdateAgent.Guards.UtcClock.Now,
        };

        var json = UpdateAgentJson.Serialize(marker);
        // 두 마커는 항상 같은 nonce여야 한다. 동시에 쓰면 파일 간 불일치/-share 오류가
        // 발생해 재부팅 후 검증이 실패하므로 체크리스트와 같은 잠금으로 직렬화한다.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(_markerPath, json, cancellationToken);
            // 보조 마커 쓰기는 선택 사항이 아니다. 실패를 숨기면 재부팅 후 검증이
            // 오래된 마커를 근거로 통과할 수 있으므로 호출자에게 실패를 전달한다.
            await File.WriteAllTextAsync(_stableMarkerPath, json, cancellationToken);
            return marker;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MarkerVerificationResult> VerifyAfterRebootAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        RestoreMarker? marker = null;
        try
        {
            if (!File.Exists(_markerPath))
            {
                return MarkerVerificationResult.Failed(
                    "재부팅 후 작업 마커가 사라졌습니다. Maestro 복원이 아직 활성 상태일 가능성이 높습니다. " +
                    "업데이트가 유지되지 않으므로 작업을 중단했습니다.");
            }

            var json = await File.ReadAllTextAsync(_markerPath, cancellationToken);
            marker = UpdateAgentJson.Deserialize<RestoreMarker>(json);
        }
        catch (Exception ex)
        {
            return MarkerVerificationResult.Failed($"마커를 읽을 수 없습니다: {ex.Message}");
        }

        if (marker is null)
            return MarkerVerificationResult.Failed("마커 파일이 손상되었습니다.");

        if (!string.Equals(marker.JobId, jobId, StringComparison.OrdinalIgnoreCase))
        {
            return MarkerVerificationResult.Failed(
                $"마커의 작업 ID({marker.JobId})가 현재 작업({jobId})과 다릅니다. 복원이 작업 상태를 되돌렸을 수 있습니다.");
        }

        // 보조 마커가 있으면 nonce를 비교해 위조/부분 복원 여부를 확인한다.
        // 손상되었거나 읽을 수 없는 보조 마커는 확인 불가이므로 통과시키지 않는다.
        if (File.Exists(_stableMarkerPath))
        {
            try
            {
                var stableJson = await File.ReadAllTextAsync(_stableMarkerPath, cancellationToken);
                var stable = UpdateAgentJson.Deserialize<RestoreMarker>(stableJson);
                if (stable is null)
                {
                    return MarkerVerificationResult.Failed(
                        "보조 마커를 읽을 수 없습니다. 마커 파일이 손상되었습니다.");
                }

                if (!string.Equals(stable.Nonce, marker.Nonce, StringComparison.Ordinal))
                {
                    return MarkerVerificationResult.Failed(
                        "보조 마커와 기본 마커의 내용이 다릅니다. 복원/상태 불일치가 의심됩니다.");
                }
            }
            catch (Exception ex)
            {
                return MarkerVerificationResult.Failed(
                    $"보조 마커를 읽을 수 없습니다: {ex.Message}");
            }
        }

        return MarkerVerificationResult.Ok(marker);
    }

    public async Task<MaestroChecklist> UpdateMarkerStatusAsync(
        string jobId,
        MarkerVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadChecklistsAsync(cancellationToken);
            var current = all.TryGetValue(jobId, out var existing)
                ? existing
                : new MaestroChecklist { JobId = jobId };

            var updated = current with
            {
                MarkerStatus = verification.Verified
                    ? MaestroVerificationStatus.MarkerVerified
                    : MaestroVerificationStatus.VerificationFailed,
                MarkerVerifiedAt = verification.CheckedAt,
                MarkerMessage = verification.Message,
            };
            all[jobId] = updated;
            await SaveChecklistsAsync(all, cancellationToken);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, MaestroChecklist>> LoadChecklistsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_checklistPath)) return new Dictionary<string, MaestroChecklist>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(_checklistPath, cancellationToken);
        var loaded = UpdateAgentJson.Deserialize<Dictionary<string, MaestroChecklist>>(json);
        // 손상된 파일을 빈 사전으로 바꾸면 이후 확인 저장이 다른 작업의 확인을 지운다.
        // 읽기/파싱 실패는 그대로 전달해 파일을 덮어쓰지 않도록 한다.
        if (loaded is null)
        {
            throw new InvalidDataException("Maestro 확인 목록 파일이 비어 있거나 손상되었습니다.");
        }

        // System.Text.Json은 기본 비교자(대소문자 구분)로 사전을 만들므로
        // 저장된 작업 ID의 대소문자가 달라도 동일한 항목으로 취급하도록 다시 감싼다.
        return new Dictionary<string, MaestroChecklist>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveChecklistsAsync(
        Dictionary<string, MaestroChecklist> all,
        CancellationToken cancellationToken)
    {
        var json = UpdateAgentJson.Serialize(all);
        await File.WriteAllTextAsync(_checklistPath, json, cancellationToken);
    }
}
