using UpdateAgent.SessionHelper;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class OverlayHotkeyTests
{
    [Theory]
    [InlineData(KeyboardBlocker.VkLWin, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkRWin, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkF4, true, false, false, false, true)]
    [InlineData(KeyboardBlocker.VkTab, true, false, false, false, true)]
    [InlineData(KeyboardBlocker.VkEscape, false, true, false, false, true)]
    [InlineData(KeyboardBlocker.VkEscape, false, true, true, false, true)]
    [InlineData(KeyboardBlocker.VkD, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkM, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkD, false, false, false, false, false)]
    public void ShouldBlockKey_SuppressesSystemShortcutsOnly(
        int vk,
        bool alt,
        bool ctrl,
        bool shift,
        bool win,
        bool expected)
    {
        Assert.Equal(expected, KeyboardBlocker.ShouldBlockKey(vk, alt, ctrl, shift, win));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    public void IsCombo_RequiresCtrlShiftUOnly(
        bool ctrl, bool shift, bool alt, bool win, bool expected)
    {
        Assert.Equal(expected,
            EmergencyStopDetector.IsCombo(KeyboardBlocker.VkU, ctrl, shift, alt, win));
    }

    [Fact]
    public void IsCombo_RejectsOtherKeys()
    {
        Assert.False(EmergencyStopDetector.IsCombo(KeyboardBlocker.VkM, true, true, false, false));
    }

    [Fact]
    public void Overlay_ShowsReportedPhaseInsteadOfGenericStateLabel()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(new JobSummary
        {
            JobId = "job-1",
            State = UpdateJobState.Cancelled,
            Phase = "강제 중단됨",
        });

        Assert.Equal("강제 중단됨", viewModel.Phase);
    }

    [Fact]
    public void Overlay_FallsBackToStateLabelWhenPhaseMissing()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(new JobSummary
        {
            JobId = "job-1",
            State = UpdateJobState.Cancelled,
            Phase = "",
        });

        Assert.Equal("업데이트 취소됨", viewModel.Phase);
    }

    [Fact]
    public void EmergencyStopDetector_RequiresTwoPressesWithinWindow()
    {
        var armed = 0;
        var triggered = 0;
        var detector = new EmergencyStopDetector(windowMs: 2000);
        detector.Armed += () => armed++;
        detector.Triggered += () => triggered++;

        Assert.False(detector.RegisterPress(1_000));
        Assert.Equal(1, armed);
        Assert.Equal(0, triggered);

        Assert.True(detector.RegisterPress(2_000));
        Assert.Equal(1, triggered);

        // 발동 후에는 초기화되어 다시 두 번 눌러야 한다.
        Assert.False(detector.RegisterPress(2_100));
        Assert.Equal(2, armed);
        Assert.True(detector.RegisterPress(3_000));
        Assert.Equal(2, triggered);
    }

    [Fact]
    public void EmergencyStopDetector_OutOfWindowRestartsCounter()
    {
        var armed = 0;
        var triggered = 0;
        var detector = new EmergencyStopDetector(windowMs: 2000);
        detector.Armed += () => armed++;
        detector.Triggered += () => triggered++;

        Assert.False(detector.RegisterPress(1_000));
        Assert.False(detector.RegisterPress(5_000)); // 창 초과 → 첫 입력으로 재시작
        Assert.Equal(2, armed);
        Assert.Equal(0, triggered);
        Assert.True(detector.RegisterPress(5_500));
        Assert.Equal(1, triggered);
    }

    [Fact]
    public void EmergencyStopDetector_ResetClearsPendingPress()
    {
        var triggered = 0;
        var detector = new EmergencyStopDetector(windowMs: 2000);
        detector.Triggered += () => triggered++;

        detector.RegisterPress(1_000);
        detector.Reset();
        Assert.False(detector.RegisterPress(1_100));
        Assert.Equal(0, triggered);
    }
}
