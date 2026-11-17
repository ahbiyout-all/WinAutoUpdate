using System.Reflection;
using System.Runtime.InteropServices;
using UpdateAgent.Core.Sessions;
using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public void SessionLauncher_UsesUnicodeStartupInfo()
    {
        var startupInfo = typeof(SessionLauncher).GetNestedType("StartupInfo", BindingFlags.NonPublic);
        Assert.NotNull(startupInfo);
        Assert.NotNull(startupInfo.StructLayoutAttribute);
        Assert.Equal(CharSet.Unicode, startupInfo.StructLayoutAttribute!.CharSet);

        var createProcess = typeof(SessionLauncher).GetMethod(
            "CreateProcessAsUser",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createProcess);

        var dllImport = createProcess.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(dllImport);
        Assert.Equal("advapi32.dll", dllImport.Value);
        Assert.Equal(CharSet.Unicode, dllImport.CharSet);
    }

    [Fact]
    public void BrowserLauncher_UsesAppModeWithTheDedicatedProfile()
    {
        var startInfo = BrowserLauncher.BuildStartInfo(
            @"C:\Fake\msedge.exe",
            "http://localhost:5080",
            1280.9,
            800.9);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(@"C:\Fake\msedge.exe", startInfo.FileName);

        var arguments = startInfo.ArgumentList.ToArray();
        Assert.Equal(6, arguments.Length);
        Assert.Equal("--app=http://localhost:5080", arguments[0]);
        Assert.Equal("--window-size=1280,800", arguments[1]);
        Assert.Equal($"--user-data-dir={BrowserLauncher.GetProfilePath()}", arguments[2]);
        Assert.Equal("--no-first-run", arguments[3]);
        Assert.Equal("--no-default-browser-check", arguments[4]);
        Assert.Equal("--disable-search-engine-choice-screen", arguments[5]);
    }

    [Fact]
    public void BrowserProfile_IsUnderTheDedicatedLocalAppDataFolder()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UpdateAgent",
            "LabManager",
            "browser-profile");

        Assert.Equal(expected, BrowserLauncher.GetProfilePath());
    }

    [Fact]
    public void AgentConfigPath_IsProgramDataAgentJson()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UpdateAgent",
            "agent.json");

        Assert.Equal(expected, UpdateAgentConstants.ConfigFilePath);
    }
}
