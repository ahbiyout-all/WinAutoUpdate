using System.Windows;
using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.LabManager.Desktop.Views;

namespace UpdateAgent.LabManager.Desktop;

/// <summary>
/// 네이티브 WPF 데스크톱 클라이언트 진입점.
/// 창을 닫으면 ShutdownMode에 의해 앱이 종료되고, 이 앱이 띄운 서버도 함께 정리된다.
/// </summary>
public partial class App : Application
{
    public static DesktopSettings Settings { get; private set; } = null!;
    public static ApiClient Api { get; private set; } = null!;
    public static LabManagerServer Server { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = new DesktopSettings();
        Api = new ApiClient(Settings);
        Server = new LabManagerServer(Settings);

        bool ready;
        try
        {
            ready = await Server.EnsureRunningAsync();
        }
        catch
        {
            ready = false;
        }

        if (!ready)
        {
            MessageBox.Show(
                $"LabManager 서버에 연결할 수 없습니다.\n\n주소: {Settings.ServerUrl}\n\n" +
                "서버가 실행 중인지 확인한 뒤 앱을 다시 실행하세요.",
                "UpdateAgent LabManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (!Settings.HasAdminToken)
        {
            var tokenDialog = new TokenDialog(requireToken: true);
            if (tokenDialog.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            Api.RefreshConfiguration();
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Server?.Stop();
        Settings?.Save();
        base.OnExit(e);
    }
}
