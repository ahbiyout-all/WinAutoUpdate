using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateAgent.Core.Comms;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.SessionHelper;

/// <summary>
/// 전체화면 검은 오버레이. 소프트 입력 잠금만 수행한다.
/// BlockInput/키보드 필터/HID 비활성화는 사용하지 않으며 Ctrl+Alt+Del은 항상 허용된다.
/// </summary>
public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly OverlayViewModel _viewModel = new();
    private readonly LocalControlPipeClient _localControl = new();
    private readonly int _sessionId;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _cursorTimer;
    private DispatcherTimer? _noticeTimer;
    private KeyboardBlocker? _keyboardBlocker;
    private bool _allowClose;

    public MainWindow(int sessionId)
    {
        InitializeComponent();
        _sessionId = sessionId;
        DataContext = _viewModel;
        SessionText.Text = sessionId > 0 ? $"session {sessionId}" : "";

        _topmostTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => ReassertTopmost(), Dispatcher);
        _cursorTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => HideCursor(), Dispatcher);

        Closing += OnClosing;
        Deactivated += (_, _) => ReassertTopmost();
        MouseMove += (_, _) => HideCursor();
    }

    public void ApplyMessage(OverlayMessage message)
    {
        switch (message.Kind)
        {
            case OverlayMessageKind.Show:
            case OverlayMessageKind.Update:
                if (message.Summary is { } summary)
                {
                    _viewModel.Apply(summary);
                }

                ShowOverlay();
                break;

            case OverlayMessageKind.Hide:
                HideOverlay(message.Notice);
                break;

            case OverlayMessageKind.Ping:
                break;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 작업 표시줄/Alt+Tab 목록에 나타나지 않게 한다.
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(style | WsExToolWindow | WsExNoActivate));
    }

    private void ShowOverlay()
    {
        _noticeTimer?.Stop();
        // 이전 안내가 4초 타이머 중간에 남아 있으면 새 오버레이에 그대로 보인다.
        _viewModel.ClearNotice();
        if (!IsVisible)
        {
            PositionWindow();
            Show();
        }

        Topmost = true;
        ReassertTopmost();
        HideCursor();

        if (_keyboardBlocker is null)
        {
            try
            {
                _keyboardBlocker = new KeyboardBlocker();
                _keyboardBlocker.EmergencyArmed += OnEmergencyArmed;
                _keyboardBlocker.EmergencyStopTriggered += OnEmergencyStopTriggered;
            }
            catch (Exception ex)
            {
                // 훅 설치 실패는 소프트 입력 잠금이 없다는 뜻이므로 조용히 성공으로 보고하지 않는다.
                System.Diagnostics.Debug.WriteLine(
                    $"[UpdateAgent.SessionHelper] 키보드 훅 설치 실패: {ex.GetType().Name}");
                HideOverlay("입력 잠금을 시작하지 못했습니다. 서비스 로그를 확인하세요.");
                return;
            }
        }
        _topmostTimer.Start();
        _cursorTimer.Start();
    }

    internal void HideOverlay(string? notice = null)
    {
        _topmostTimer.Stop();
        _cursorTimer.Stop();
        if (_keyboardBlocker is not null)
        {
            _keyboardBlocker.EmergencyArmed -= OnEmergencyArmed;
            _keyboardBlocker.EmergencyStopTriggered -= OnEmergencyStopTriggered;
            _keyboardBlocker.Dispose();
            _keyboardBlocker = null;
        }

        // 입력 잠금은 즉시 해제한다.
        Mouse.OverrideCursor = null;

        // 안내가 있으면 짧게 보여준 뒤 숨긴다. 안내가 없으면(자동 숨김/재동기화) 즉시 숨긴다.
        if (!string.IsNullOrWhiteSpace(notice) && IsVisible)
        {
            _viewModel.SetNotice(notice);
            _noticeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal,
                (_, _) => FinishHide(), Dispatcher);
            _noticeTimer.Stop();
            _noticeTimer.Start();
            return;
        }

        FinishHide();
    }

    private void FinishHide()
    {
        _noticeTimer?.Stop();
        _viewModel.ClearNotice();
        Hide();
    }

    private void OnEmergencyArmed() =>
        Dispatcher.BeginInvoke(() =>
            _viewModel.SetNotice("긴급 중단: Ctrl+Shift+U를 한 번 더 누르면 업데이트를 중단합니다."));

    private void OnEmergencyStopTriggered() =>
        Dispatcher.BeginInvoke(() => _ = SendEmergencyStopAsync());

    private async Task SendEmergencyStopAsync()
    {
        try
        {
            _viewModel.SetNotice("긴급 중단 요청을 보냈습니다.");
            var response = await _localControl.SendAsync(new LocalControlRequest
            {
                Action = LocalControlActions.CancelUpdate,
            }).ConfigureAwait(true);

            // 서비스가 준 상세 메시지는 서비스 로그에 남고, 학생 화면에는 고정 문구만 보여준다.
            _viewModel.SetNotice(response.Success
                ? "긴급 중단 요청이 처리되었습니다."
                : "중단 요청을 처리하지 못했습니다.");
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..8];
            System.Diagnostics.Debug.WriteLine(
                $"[UpdateAgent.SessionHelper] 중단 요청 전송 실패 ({errorId}): {ex}");
            _viewModel.SetNotice("중단 요청을 전송하지 못했습니다.");
        }
    }

    private void PositionWindow()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);

        // 콘텐츠는 주 모니터 중앙에 배치한다.
        // WPF 좌표에서 주 모니터의 원점은 항상 (0,0)이다.
        var left = -SystemParameters.VirtualScreenLeft;
        var top = -SystemParameters.VirtualScreenTop;
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                    - SystemParameters.PrimaryScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight
                     - SystemParameters.PrimaryScreenHeight;

        RootGrid.Margin = new Thickness(
            Math.Max(0, left),
            Math.Max(0, top),
            Math.Max(0, right),
            Math.Max(0, bottom));
    }

    private void ReassertTopmost()
    {
        if (!IsVisible) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle,
            HwndTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void HideCursor()
    {
        if (!IsVisible) return;
        Mouse.OverrideCursor = Cursors.None;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;

        // Alt+F4/작업 관리자 종료로 오버레이가 사라지지 않게 한다.
        // 서비스가 Hide 메시지를 보내면 숨기기만 하고 프로세스는 유지된다.
        e.Cancel = true;
        HideOverlay();
    }

    protected override void OnClosed(EventArgs e)
    {
        _allowClose = true;
        _keyboardBlocker?.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Win/Alt 조합 등 일부 단축키만 차단하는 저수준 키보드 훅.</summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    internal const int VkTab = 0x09;
    internal const int VkEscape = 0x1B;
    internal const int VkF4 = 0x73;
    internal const int VkLWin = 0x5B;
    internal const int VkRWin = 0x5C;
    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12;
    internal const int VkD = 0x44;
    internal const int VkM = 0x4D;
    internal const int VkU = 0x55;

    private const int KfRepeat = 0x80;

    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly EmergencyStopDetector _emergency = new();
    private IntPtr _hook;

    /// <summary>Ctrl+Shift+U 첫 입력. 두 번째 입력 전까지 안내를 표시한다.</summary>
    internal event Action? EmergencyArmed;

    /// <summary>Ctrl+Shift+U 두 번 입력. 오버레이가 긴급 중단 요청을 보낸다.</summary>
    internal event Action? EmergencyStopTriggered;

    public KeyboardBlocker()
    {
        _emergency.Armed += () => EmergencyArmed?.Invoke();
        _emergency.Triggered += () => EmergencyStopTriggered?.Invoke();
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc,
            NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("저수준 키보드 훅을 설치하지 못했습니다.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);

            // 키를 누른 채 반복되는 자동 반복 입력은 중단 카운트로 세지 않는다.
            if ((info.flags & KfRepeat) == 0 &&
                EmergencyStopDetector.IsCombo(
                    info.vkCode,
                    IsDown(VkControl),
                    IsDown(VkShift),
                    IsDown(VkMenu),
                    IsDown(VkLWin) || IsDown(VkRWin)))
            {
                _emergency.RegisterPress(Environment.TickCount64);
            }

            if (ShouldBlock(info.vkCode))
            {
                // 반환값 1: 메시지를 삼킨다(저수준 훅만 사용하며 BlockInput은 하지 않는다).
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(int vk) =>
        ShouldBlockKey(
            vk,
            IsDown(VkMenu),
            IsDown(VkControl),
            IsDown(VkShift),
            IsDown(VkLWin) || IsDown(VkRWin));

    /// <summary>키와 보조키 상태만으로 차단 여부를 판단한다(훅 콜백과 테스트가 공유).</summary>
    internal static bool ShouldBlockKey(int vk, bool alt, bool ctrl, bool shift, bool win)
    {
        // Windows 키 자체를 막아 시작 메뉴/단축키를 차단한다.
        if (vk is VkLWin or VkRWin) return true;

        // Alt+F4, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc
        if (vk == VkF4 && alt) return true;
        if (vk == VkTab && alt) return true;
        if (vk == VkEscape && alt) return true;
        if (vk == VkEscape && ctrl && !shift) return true;
        if (vk == VkEscape && ctrl && shift) return true;

        // Win+D(바탕 화면), Win+M(최소화)
        if (win && vk is VkD or VkM) return true;

        return false;
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// 지정한 시간 창 안에 Ctrl+Shift+U를 두 번 눌러야만 발동하는 긴급 중단 감지기.
/// 시간 값을 인자로 받아 단위 테스트에서 결정적으로 검증할 수 있다.
/// </summary>
internal sealed class EmergencyStopDetector
{
    private const long NoPress = long.MinValue;
    private readonly long _windowMs;
    private long _firstPressAt = NoPress;

    public EmergencyStopDetector(long windowMs = 3000) => _windowMs = windowMs;

    internal event Action? Armed;
    internal event Action? Triggered;

    internal static bool IsCombo(int vk, bool ctrl, bool shift, bool alt, bool win) =>
        vk == KeyboardBlocker.VkU && ctrl && shift && !alt && !win;

    /// <summary>조합키 입력을 등록한다. true를 반환하면 두 번째 입력으로 발동된 것이다.</summary>
    internal bool RegisterPress(long nowTicks)
    {
        if (_firstPressAt == NoPress || nowTicks - _firstPressAt > _windowMs)
        {
            _firstPressAt = nowTicks;
            Armed?.Invoke();
            return false;
        }

        _firstPressAt = NoPress;
        Triggered?.Invoke();
        return true;
    }

    internal void Reset() => _firstPressAt = NoPress;
}

internal static partial class NativeMethods
{
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
