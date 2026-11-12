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
                ProtectionSuspendedConfirmedAt = DateTimeOffset.Now,
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
                ProtectionRestoredConfirmedAt = DateTimeOffset.Now,
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
            WrittenAt = DateTimeOffset.Now,
        };

        var json = UpdateAgentJson.Serialize(marker);
        await File.WriteAllTextAsync(_markerPath, json, cancellationToken);
        try
        {
            await File.WriteAllTextAsync(_stableMarkerPath, json, cancellationToken);
        }
        catch
        {
            // 보조 경로 쓰기 실패는 무시한다. 기본 마커만으로 검증한다.
        }

        return marker;
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
        try
        {
            if (File.Exists(_stableMarkerPath))
            {
                var stableJson = await File.ReadAllTextAsync(_stableMarkerPath, cancellationToken);
                var stable = UpdateAgentJson.Deserialize<RestoreMarker>(stableJson);
                if (stable is not null
                    && !string.Equals(stable.Nonce, marker.Nonce, StringComparison.Ordinal))
                {
                    return MarkerVerificationResult.Failed(
                        "보조 마커와 기본 마커의 내용이 다릅니다. 복원/상태 불일치가 의심됩니다.");
                }
            }
        }
        catch
        {
            // 보조 마커 오류는 무시한다.
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
        try
        {
            var json = await File.ReadAllTextAsync(_checklistPath, cancellationToken);
            return UpdateAgentJson.Deserialize<Dictionary<string, MaestroChecklist>>(json)
                   ?? new Dictionary<string, MaestroChecklist>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, MaestroChecklist>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveChecklistsAsync(
        Dictionary<string, MaestroChecklist> all,
        CancellationToken cancellationToken)
    {
        var json = UpdateAgentJson.Serialize(all);
        await File.WriteAllTextAsync(_checklistPath, json, cancellationToken);
    }
}
