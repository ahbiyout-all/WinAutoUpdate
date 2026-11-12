using System.Windows;
using UpdateAgent.Core.Comms;

namespace UpdateAgent.SessionHelper;

public partial class App : Application
{
    private readonly CancellationTokenSource _cts = new();
    private OverlayPipeClient? _client;

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

        var window = new MainWindow(sessionId);
        MainWindow = window;
        window.Hide();

        var client = new OverlayPipeClient();
        client.MessageReceived += message =>
        {
            Dispatcher.Invoke(() => window.ApplyMessage(message));
            return Task.CompletedTask;
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
        base.OnExit(e);
    }
}
