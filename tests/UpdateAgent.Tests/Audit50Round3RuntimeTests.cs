using System.Reflection;

namespace UpdateAgent.Tests;

/// <summary>50회차 3차 감사에서 확인한 런타임 결함의 회귀 테스트.</summary>
public sealed class Audit50Round3RuntimeTests
{
    private const string RunningWithStaleExitCode = """
        SERVICE_NAME: UpdateAgentService
                TYPE               : 10  WIN32_OWN_PROCESS
                STATE              : 4  RUNNING
                WIN32_EXIT_CODE    : 1  (0x1)
                SERVICE_EXIT_CODE  : 0  (0x0)
                CHECKPOINT         : 0x0
                WAIT_HINT          : 0x0
        """;

    private const string StoppedWithStaleExitCode = """
        SERVICE_NAME: UpdateAgentService
                TYPE               : 10  WIN32_OWN_PROCESS
                STATE              : 1  STOPPED
                WIN32_EXIT_CODE    : 4  (0x4)
                SERVICE_EXIT_CODE  : 0  (0x0)
                CHECKPOINT         : 0x0
                WAIT_HINT          : 0x0
        """;

    // sc.exe 출력의 WIN32_EXIT_CODE는 마지막 오류 코드일 뿐 서비스 상태가 아니다.
    [Fact]
    public void ResolveServiceStatus_RunningStateIgnoresExitCodeField() =>
        Assert.Equal("실행 중", ResolveServiceStatus(RunningWithStaleExitCode, 0));

    [Fact]
    public void ResolveServiceStatus_StoppedStateIgnoresExitCodeField() =>
        Assert.Equal("중지됨", ResolveServiceStatus(StoppedWithStaleExitCode, 0));

    [Fact]
    public void ResolveServiceStatus_QueryFailureAndUnknownStateStayUnknown()
    {
        Assert.Equal("설치되지 않음", ResolveServiceStatus(RunningWithStaleExitCode, 1060));
        Assert.Equal("상태 확인 필요", ResolveServiceStatus("SERVICE_NAME: UpdateAgentService", 0));
        Assert.Equal("상태 확인 필요", ResolveServiceStatus("STATE : 3  STOP_PENDING", 0));
    }

    private static string ResolveServiceStatus(string output, int exitCode)
    {
        var root = FindRepositoryRoot();
        var currentConfiguration = AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        foreach (var configuration in new[] { currentConfiguration,
                     currentConfiguration == "Release" ? "Debug" : "Release" })
        {
            var path = Path.Combine(
                root, "src", "UpdateAgent.Agent.Desktop", "bin", configuration,
                "net10.0-windows", "UpdateAgent.Agent.Desktop.dll");
            if (!File.Exists(path)) continue;

            // 기본 로드 컨텍스트에 이미 로드된 Desktop DLL(기존 테스트가 LoadFrom으로 로드)과
            // 충돌하지 않도록 파일별 격리 컨텍스트로 읽는다.
            var assembly = Assembly.LoadFile(path);
            var type = assembly.GetType(
                "UpdateAgent.Agent.Desktop.Services.AgentStatusReader", throwOnError: true)!;
            var method = type.GetMethod(
                "ResolveServiceStatus",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string)method.Invoke(null, [output, exitCode])!;
        }

        throw new FileNotFoundException("빌드된 UpdateAgent.Agent.Desktop.dll을 찾을 수 없습니다.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("UpdateAgent.sln").Length > 0) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("UpdateAgent.sln을 찾을 수 없습니다.");
    }
}
