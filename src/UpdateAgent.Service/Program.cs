using Microsoft.Extensions.Options;
using Serilog;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Service;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Comms;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Overlay;
using UpdateAgent.Service.Sessions;
using UpdateAgent.Shared;

var dataDirectory = UpdateAgentConstants.DataDirectory;
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("UpdateAgent.Service 구성 중...");

    var builder = Host.CreateApplicationBuilder(args);

    // MSI 배포용 머신 설정: %ProgramData%\UpdateAgent\agent.json
    // appsettings.json보다 우선하고, 환경 변수/명령줄보다는 낮은 우선순위를 유지한다.
    builder.Configuration
        .AddJsonFile(UpdateAgentConstants.ConfigFilePath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .AddCommandLine(args);

    builder.Services.Configure<AgentOptions>(
        builder.Configuration.GetSection(AgentOptions.SectionName));

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = UpdateAgentConstants.ServiceDisplayName;
    });

    builder.Services.AddSerilog(Log.Logger, dispose: false);

    builder.Services.AddHttpClient("LabManager", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Add("User-Agent", $"UpdateAgent/{UpdateAgentConstants.AgentVersion}");
    });

    // Windows Update / 로컬 인프라
    builder.Services.AddSingleton<IWuaClient, WuaClient>();
    builder.Services.AddSingleton<IJobStore, SqliteJobStore>();
    builder.Services.AddSingleton<IPreflightChecker>(sp => new WindowsPreflightChecker(
        sp.GetRequiredService<IWuaClient>(),
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.DryRun));
    builder.Services.AddSingleton<IRestorationProvider, ManualMaestroProvider>();
    builder.Services.AddSingleton<IRebootManager, WindowsRebootManager>();
    builder.Services.AddSingleton<IUserSessionProbe, UserSessionProbe>();
    builder.Services.AddSingleton<SessionLauncher>();

    // 오버레이
    builder.Services.AddSingleton<OverlayPipeServer>();
    builder.Services.AddSingleton<OverlayJobProgressSink>();
    builder.Services.AddSingleton<IJobProgressSink>(
        sp => sp.GetRequiredService<OverlayJobProgressSink>());

    // 작업 러너
    builder.Services.AddSingleton(sp => new UpdateJobRunner(
        sp.GetRequiredService<IWuaClient>(),
        sp.GetRequiredService<IJobStore>(),
        sp.GetRequiredService<IPreflightChecker>(),
        sp.GetRequiredService<IRestorationProvider>(),
        sp.GetRequiredService<IRebootManager>(),
        sp.GetRequiredService<IUserSessionProbe>(),
        sp.GetRequiredService<IJobProgressSink>(),
        sp.GetRequiredService<ILogger<UpdateJobRunner>>(),
        new UpdateJobRunnerOptions
        {
            DryRun = sp.GetRequiredService<IOptions<AgentOptions>>().Value.DryRun,
        }));

    // 원격 관리 + 학생용 앱 로컬 제어
    builder.Services.AddSingleton<ServerPolicyProvider>();
    builder.Services.AddSingleton<LabManagerClient>();
    builder.Services.AddSingleton<CommandExecutor>();
    builder.Services.AddSingleton<SessionWatcherService>();
    builder.Services.AddSingleton(sp =>
    {
        var runner = sp.GetRequiredService<UpdateJobRunner>();
        var trigger = new LocalUpdateTrigger(
            (policy, confirmed, by, ct) => runner.StartAsync(policy, confirmed, by, null, ct),
            sp.GetRequiredService<ServerPolicyProvider>(),
            sp.GetRequiredService<ILogger<LocalUpdateTrigger>>());
        return new LocalControlPipeServer(trigger.HandleAsync);
    });

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "UpdateAgent.Service가 비정상 종료되었습니다.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
