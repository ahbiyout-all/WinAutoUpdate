using System.Windows;
using UpdateAgent.LabManager.Desktop.Services;

namespace UpdateAgent.LabManager.Desktop;

/// <summary>
/// 교사용 런처. 창을 띄우지 않고 로컬 LabManager 서버를 준비한 뒤
/// Edge/Chrome의 --app= 단독 창 모드로 대시보드를 연다.
/// 서버는 앱 모드 창을 닫아도 계속 실행되어 학생 PC 체크인을 유지한다.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            ShowError(
                $"LabManager 서버에 연결할 수 없습니다.\n\n주소: {settings.ServerUrl}\n\n" +
                "서버가 실행 중인지 확인한 뒤 다시 실행하세요.");
            Shutdown();
            return;
        }

        if (!BrowserLauncher.OpenDashboard(settings.DashboardUrl, settings.Window.Width, settings.Window.Height))
        {
            ShowError(
                "Microsoft Edge 또는 Google Chrome을 찾을 수 없습니다.\n\n" +
                "브라우저를 설치한 뒤 다시 실행하세요.");
            Shutdown();
            return;
        }

        // 의도적으로 Stop()을 호출하지 않는다. 서버는 백그라운드에 남는다.
        Shutdown();
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "UpdateAgent LabManager", MessageBoxButton.OK, MessageBoxImage.Error);
}
