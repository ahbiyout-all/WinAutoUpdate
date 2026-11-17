using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly int _sessionId;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _cursorTimer;
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
                HideOverlay();
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
        if (!IsVisible)
        {
            PositionWindow();
            Show();
        }

        Topmost = true;
        ReassertTopmost();
        HideCursor();

        _keyboardBlocker ??= new KeyboardBlocker();
        _topmostTimer.Start();
        _cursorTimer.Start();
    }

    internal void HideOverlay()
    {
        _topmostTimer.Stop();
        _cursorTimer.Stop();
        _keyboardBlocker?.Dispose();
        _keyboardBlocker = null;

        Mouse.OverrideCursor = null;
        Hide();
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

    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkF4 = 0x73;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkD = 0x44;
    private const int VkM = 0x4D;

    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public KeyboardBlocker()
    {
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc,
            NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            if (ShouldBlock(info.vkCode))
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(int vk)
    {
        // Windows 키 자체를 막아 시작 메뉴/단축키를 차단한다.
        if (vk is VkLWin or VkRWin) return true;

        var alt = IsDown(VkMenu);
        var ctrl = IsDown(VkControl);
        var shift = IsDown(VkShift);
        var win = IsDown(VkLWin) || IsDown(VkRWin);

        // Alt+F4, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc
        if (vk == VkF4 && alt) return true;
        if (vk == VkTab && alt) return true;
        if (vk == VkEscape && alt) return true;
        if (vk == VkEscape && ctrl) return true;

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
