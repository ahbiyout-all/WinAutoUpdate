using System.Text.Json;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

public sealed class MaestroMarkerTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _stableDir;
    private readonly string? _originalEnv;

    public MaestroMarkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "data");
        _stableDir = Path.Combine(_root, "stable");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_stableDir);

        _originalEnv = Environment.GetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable);
        Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, _stableDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, _originalEnv);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 테스트 정리 실패는 무시한다.
        }
    }

    private ManualMaestroProvider CreateProvider() => new(_dataDir);

    [Fact]
    public async Task Marker_SurvivesVerification()
    {
        var provider = CreateProvider();
        var marker = await provider.WriteResumeMarkerAsync("job-1", "agent-1", bootCount: 1, phase: "installing");

        var result = await provider.VerifyAfterRebootAsync("job-1");

        Assert.True(result.Verified, result.Message);
        Assert.NotNull(result.Marker);
        Assert.Equal(marker.Nonce, result.Marker!.Nonce);
        Assert.Equal("job-1", result.Marker.JobId);
    }

    [Fact]
    public async Task MissingMarker_FailsVerification()
    {
        var provider = CreateProvider();

        var result = await provider.VerifyAfterRebootAsync("job-1");

        Assert.False(result.Verified);
        Assert.Contains("마커가 사라졌습니다", result.Message);
    }

    [Fact]
    public async Task MarkerForDifferentJob_FailsVerification()
    {
        var provider = CreateProvider();
        await provider.WriteResumeMarkerAsync("job-1", "agent-1", 1, "installing");

        var result = await provider.VerifyAfterRebootAsync("job-2");

        Assert.False(result.Verified);
        Assert.Contains("작업 ID", result.Message);
    }

    [Fact]
    public async Task MismatchedStableMarker_FailsVerification()
    {
        var provider = CreateProvider();
        await provider.WriteResumeMarkerAsync("job-1", "agent-1", 1, "installing");

        // 보조 마커만 다른 nonce로 바뀐 상황 (부분 복원)을 재현한다.
        var tampered = new RestoreMarker
        {
            JobId = "job-1",
            AgentId = "agent-1",
            Nonce = Guid.NewGuid().ToString("N"),
            BootCount = 1,
            Phase = "installing",
        };
        await File.WriteAllTextAsync(
            Path.Combine(_stableDir, "stable-resume-marker.json"),
            UpdateAgentJson.Serialize(tampered));

        var result = await provider.VerifyAfterRebootAsync("job-1");

        Assert.False(result.Verified);
        Assert.Contains("보조 마커", result.Message);
    }

    [Fact]
    public async Task ChecklistConfirmations_ArePersisted()
    {
        var provider = CreateProvider();

        await provider.ConfirmProtectionSuspendedAsync("job-1", "teacher");
        await provider.ConfirmRestoredAsync("job-1", stateSaved: true, protectionRestored: true);

        var checklist = await provider.GetChecklistAsync("job-1");

        Assert.True(checklist.ProtectionSuspendedConfirmed);
        Assert.Equal("teacher", checklist.ConfirmedBy);
        Assert.True(checklist.StateSavedConfirmed);
        Assert.True(checklist.ProtectionRestoredConfirmed);
        Assert.True(checklist.CanStartJob);
    }

    [Fact]
    public async Task MarkerStatus_UpdatesChecklist()
    {
        var provider = CreateProvider();
        await provider.WriteResumeMarkerAsync("job-1", "agent-1", 1, "installing");
        var verification = await provider.VerifyAfterRebootAsync("job-1");

        var checklist = await provider.UpdateMarkerStatusAsync("job-1", verification);

        Assert.Equal(MaestroVerificationStatus.MarkerVerified, checklist.MarkerStatus);
        Assert.NotNull(checklist.MarkerVerifiedAt);
    }

    [Fact]
    public async Task CorruptedMarkerFile_FailsGracefully()
    {
        var provider = CreateProvider();
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "resume-marker.json"), "{not-json");

        var result = await provider.VerifyAfterRebootAsync("job-1");

        Assert.False(result.Verified);
        Assert.Contains("마커", result.Message);
    }
}
