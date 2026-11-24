using System.IO;
using System.Windows;
using UpdateAgent.LabManager.Desktop.Services;

namespace UpdateAgent.LabManager.Desktop;

/// <summary>
/// 교사용 런처. 창을 띄우지 않고 로컬 LabManager 서버를 준비한 뒤
/// Edge/Chrome의 --app= 단독 창 모드로 대시보드를 연다.
/// --monitor 인자를 주면 실시간 작업 현황 모니터만 연다(전용 바로가기용).
/// 서버는 앱 모드 창을 닫아도 계속 실행되어 학생 PC 체크인을 유지한다.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var monitorMode = e.Args.Any(arg =>
            arg.Equals("--monitor", StringComparison.OrdinalIgnoreCase));

        var settings = new DesktopSettings();
        var server = new LabManagerServer(settings);

        bool ready;
        try
        {
            ready = await server.EnsureRunningAsync();
        }
        catch
        {
            ready = false;
        }

        if (!ready)
        {
            var hint = server.LocalServerExited
                ? "로컬 서버가 시작되지 않았습니다. change-me-* 기본 토큰을 변경했는지 확인하세요.\n" +
                  "설정: " + Path.Combine(AppContext.BaseDirectory, "appsettings.json") + "\n" +
                  "로그: " + Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                      "UpdateAgent", "LabManager", "logs")
                : "서버가 실행 중인지 확인한 뒤 다시 실행하세요.";

            ShowError(
                $"LabManager 서버에 연결할 수 없습니다.\n\n주소: {settings.ServerUrl}\n\n{hint}");
            Shutdown();
            return;
        }

        var opened = monitorMode
            ? BrowserLauncher.OpenMonitor(settings.MonitorUrl, settings.Window.Width, settings.Window.Height)
            : BrowserLauncher.OpenDashboard(settings.DashboardUrl, settings.Window.Width, settings.Window.Height);

        if (!opened)
        {
            ShowError(
                "대시보드 브라우저 창을 열지 못했습니다.\n\n" +
                "Microsoft Edge 또는 Google Chrome 설치와 실행 권한을 확인한 뒤 다시 실행하세요.");
            Shutdown();
            return;
        }

        // 의도적으로 Stop()을 호출하지 않는다. 서버는 백그라운드에 남는다.
        Shutdown();
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "UpdateAgent LabManager", MessageBoxButton.OK, MessageBoxImage.Error);
}
