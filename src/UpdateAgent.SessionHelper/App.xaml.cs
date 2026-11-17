using System.Diagnostics;
using System.Threading;
using System.Windows;
using UpdateAgent.Core.Comms;

namespace UpdateAgent.SessionHelper;

public partial class App : Application
{
    private readonly CancellationTokenSource _cts = new();
    private OverlayPipeClient? _client;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sessionId = 0;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--session" && i + 1 < e.Args.Length)
            {
                int.TryParse(e.Args[i + 1], out sessionId);
            }
        }

        if (sessionId <= 0)
        {
            sessionId = Process.GetCurrentProcess().SessionId;
        }

        // 서비스 재시작 등으로 같은 세션에 helper가 남아 있으면 중복 실행하지 않는다.
        var mutexName = $@"Local\UpdateAgent.SessionHelper.{sessionId}";
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
        }
        catch (AbandonedMutexException ex)
        {
            // 강제 종료된 helper가 남긴 뮤텍스도 현재 스레드가 소유하므로 정상 실행으로 처리한다.
            var abandonedMutex = ex.Mutex as Mutex;
            if (abandonedMutex is null) { throw; }
            _singleInstanceMutex = abandonedMutex;
            createdNew = true;
        }

        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(sessionId);
        MainWindow = window;
        window.Hide();

        var client = new OverlayPipeClient();
        client.MessageReceived += message =>
        {
            Dispatcher.Invoke(() => window.ApplyMessage(message));
            return Task.CompletedTask;
        };

        // 서비스/파이프가 끊기면 전체화면 오버레이와 키보드 훅이 남지 않도록 즉시 해제한다.
        client.ConnectionChanged += connected =>
        {
            if (!connected)
            {
                Dispatcher.BeginInvoke(() => window.HideOverlay());
            }
        };

        _client = client;
        _ = client.RunAsync(_cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        _ = _client?.DisposeAsync();
        try
        {
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
