using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 7(필터 URL 공유/다중 모니터 동기화)의 회귀 테스트. 허용 목록 파싱,
/// 비밀값 비직렬화, history/popstate/디바운스, source id 에코 방지,
/// 클립보드 실패 안내, 한국어 UI/문서와 기존 job 딥 링크를 확인한다.
/// </summary>
public sealed class Improvement07FilterSharingTests
{
    private const string DashboardFile = "src/UpdateAgent.LabManager/wwwroot/app.js";
    private const string MonitorFile = "src/UpdateAgent.LabManager/wwwroot/monitor.js";
    private const string SecretPatternDeclaration =
        "const SENSITIVE_KEY_PATTERN = /(?:token|secret|password|passwd|pwd|api[-_]?key|auth|credential|session)/i;";
    private const string SecretKeyRegex = @"(?:token|secret|password|passwd|pwd|api[-_]?key|auth|credential|session)";
    private const int ValueMax = 64;

    private static readonly string[] MonitorStates = ["all", "busy", "attention", "offline"];

    [Fact]
    public void DashboardUrlParse_UsesAllowlist_AndSafeFallbacks()
    {
        var app = Read(DashboardFile);
        Assert.Contains(
            "const DASHBOARD_OS_IDS = new Set(['all', ...OS_GROUPS.map((group) => group.id)]);",
            app, StringComparison.Ordinal);
        Assert.Contains("function readDashboardFilters", app, StringComparison.Ordinal);
        Assert.Contains("params.getAll('os')", app, StringComparison.Ordinal);
        var allowed = ReadOsGroups(app).Append("all").ToArray();

        Assert.Equal("all", ParseOs(string.Empty, allowed));
        Assert.Equal("all", ParseOs("unknown-value", allowed));
        Assert.Equal("all", ParseOs(new string('x', ValueMax + 1), allowed));
        Assert.Equal("server", ParseOs("SERVER", allowed));
        Assert.Equal("server-core", ParseOs("server-core", allowed));
        Assert.Equal("windows-10", ParseOs(" windows-10 ", allowed));
        Assert.Contains(allowed, value => value == ParseOs(value, allowed));
    }

    [Fact]
    public void MonitorUrlParse_UsesAllowlist_AndSafeFallbacks()
    {
        var monitor = Read(MonitorFile);
        var states = ExtractArray(monitor, "const MONITOR_STATE_IDS = [");
        Assert.Equal(MonitorStates, states);
        Assert.Contains("function readMonitorFilters", monitor, StringComparison.Ordinal);
        Assert.Contains("params.getAll('q')", monitor, StringComparison.Ordinal);
        Assert.Contains("params.getAll('state')", monitor, StringComparison.Ordinal);

        foreach (var state in MonitorStates)
            Assert.Equal(state, ParseState(state.ToUpperInvariant(), states));
        Assert.Equal("all", ParseState("bogus", states));
        Assert.Equal("all", ParseState(string.Empty, states));
        Assert.Equal("all", ParseState(new string('s', ValueMax + 1), states));
        Assert.Equal("pc-01", ParseText(" PC-01 ", false));
        Assert.Equal(string.Empty, ParseText("pc<01", false));
        Assert.Equal(string.Empty, ParseText("sample-admin-token-1234", true));
        Assert.Equal(string.Empty, ParseText(new string('z', ValueMax + 1), false));
    }

    [Fact]
    public void UnknownValues_AreIgnored_AndBounded()
    {
        var app = Read(DashboardFile);
        var monitor = Read(MonitorFile);
        foreach (var source in new[] { app, monitor })
        {
            Assert.Contains("const FILTER_VALUE_MAX = 64;", source, StringComparison.Ordinal);
            Assert.Contains("빈 값은 '없음'으로 보고 경고하지 않는다.", source, StringComparison.Ordinal);
            Assert.Contains("value.length > FILTER_VALUE_MAX * 4", source, StringComparison.Ordinal);
            Assert.Contains("UNKNOWN_FILTER_NOTICE = '필터 값 확인 불가", source, StringComparison.Ordinal);
        }
        Assert.Contains("const FILTER_PAYLOAD_MAX = 512;", monitor, StringComparison.Ordinal);
        Assert.Contains("serialized.length > FILTER_PAYLOAD_MAX", monitor, StringComparison.Ordinal);
        Assert.Contains("event.newValue.length > FILTER_PAYLOAD_MAX", monitor, StringComparison.Ordinal);
        Assert.Contains("normalizeOsFilterValue(button.dataset.osFilter)", app, StringComparison.Ordinal);
        Assert.Equal("all", ParseOs("bogus", ReadOsGroups(app).Append("all").ToArray()));
        Assert.Equal("all", ParseState("bogus", MonitorStates));
    }

    [Fact]
    public void Serialization_DropsTokenAndSecretKeys()
    {
        var app = Read(DashboardFile);
        var monitor = Read(MonitorFile);
        foreach (var (source, nextFunction) in new[]
                 {
                     (app, "function dashboardFilterUrl"),
                     (monitor, "function monitorFilterUrl"),
                 })
        {
            Assert.Contains(SecretPatternDeclaration, source, StringComparison.Ordinal);
            var function = Slice(source, "function safeFilterParams", nextFunction);
            Assert.Contains("if (SENSITIVE_KEY_PATTERN.test(key)) continue;", function, StringComparison.Ordinal);
            Assert.Contains("if (value.length > FILTER_VALUE_MAX * 4) continue;", function, StringComparison.Ordinal);
            Assert.Contains("if (adminToken && value === adminToken) continue;", function, StringComparison.Ordinal);
            Assert.Contains("safe.append(key, value);", function, StringComparison.Ordinal);
        }

        var sensitive = new Regex(SecretKeyRegex, RegexOptions.IgnoreCase);
        foreach (var key in new[]
                 {
                     "token", "admin_token", "X-Admin-Token", "secret", "password", "passwd",
                     "pwd", "apiKey", "auth", "credential", "session", "ua_admin_token",
                 })
        {
            Assert.Matches(sensitive, key);
            Assert.Empty(SafeParameters([(key, "plain-value")], "plain-value"));
        }
        // 현재 토큰과 같은 값은 무해한 키 이름이라도 직렬화하지 않는다.
        Assert.Empty(SafeParameters([("note", "super-secret-value")], "super-secret-value"));
        Assert.Equal("job=j1", string.Join('&', SafeParameters([("job", "j1")], "super-secret-value")
            .Select(pair => $"{pair.Key}={pair.Value}")));
    }

    [Fact]
    public void TokenLikeValues_AreDroppedFromUrls()
    {
        foreach (var file in new[] { DashboardFile, MonitorFile })
        {
            var source = Read(file);
            Assert.Contains("if (SENSITIVE_KEY_PATTERN.test(value)) continue;", source, StringComparison.Ordinal);
        }
        // 키 이름이 무해해도 값 자체가 토큰/비밀처럼 보이면 공유 링크에 남기지 않는다.
        Assert.Empty(SafeParameters([("note", "my-admin-token-123"), ("ref", "session-secret")], "other-value"));
        Assert.Single(SafeParameters([("job", "job-20261128-120000-a1b2c3")], "other-value"));
    }

    [Fact]
    public void BroadcastPayload_HasOnlyFilterFields()
    {
        var monitor = Read(MonitorFile);
        var message = Slice(monitor, "function buildFilterMessage", "function broadcastFilters");
        Assert.Equal(
            new[] { "at", "q", "sourceId", "state", "type" },
            ExtractObjectKeys(message, "return").OrderBy(key => key, StringComparer.Ordinal).ToArray());
        var stored = Slice(monitor, "function saveFilterState", "function applyMonitorFilters");
        Assert.Equal(
            new[] { "at", "q", "sourceId", "state" },
            ExtractObjectKeys(stored, "const payload =").OrderBy(key => key, StringComparer.Ordinal).ToArray());
        foreach (var slice in new[] { message, stored })
        {
            Assert.DoesNotContain("adminToken", slice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localStorage.getItem(STORAGE_KEY)", slice, StringComparison.Ordinal);
            Assert.DoesNotContain("X-Admin-Token", slice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", slice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch", slice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("console.log", slice, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("JSON.stringify(payload).length <= FILTER_PAYLOAD_MAX", stored, StringComparison.Ordinal);
        Assert.Contains("const url = monitorFilterUrl();", Slice(monitor, "async function copyFilterLink", "\n}"), StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardHistoryWiring_UsesReplaceStateAndPopstate()
    {
        var app = Read(DashboardFile);
        var write = Slice(app, "function dashboardFilterUrl", "function applyDashboardFilters");
        Assert.Contains("history.replaceState(history.state, '', dashboardFilterUrl())", write, StringComparison.Ordinal);
        Assert.DoesNotContain("history.pushState", app, StringComparison.Ordinal);
        Assert.Contains("params.delete('os')", write, StringComparison.Ordinal);
        Assert.Contains("params.set('os', osFilter)", write, StringComparison.Ordinal);
        var wiring = Slice(app, "function wireEvents", "setupTokenDialog()");
        Assert.Contains("window.addEventListener('popstate'", wiring, StringComparison.Ordinal);
        Assert.Contains("applyDashboardFilters(readDashboardFilters(location.search), { writeUrl: false })", wiring, StringComparison.Ordinal);
        Assert.Contains("writeDashboardUrl();", Slice(app, "function renderOsFilter", "function updateDashboardFilterNotice"), StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorHistoryWiring_DebouncesAndRestores()
    {
        var monitor = Read(MonitorFile);
        Assert.Contains("const FILTER_DEBOUNCE_MS = 250;", monitor, StringComparison.Ordinal);
        Assert.Contains("history.replaceState(history.state, '', monitorFilterUrl())", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("history.pushState", monitor, StringComparison.Ordinal);
        var wiring = Slice(monitor, "function wireEvents", "ensureMonitorControls()");
        Assert.Contains("clearTimeout(filterTextTimer)", wiring, StringComparison.Ordinal);
        Assert.Contains("setTimeout(() =>", wiring, StringComparison.Ordinal);
        Assert.Contains("FILTER_DEBOUNCE_MS", wiring, StringComparison.Ordinal);
        Assert.Contains("{ writeUrl: true, broadcast: true }", wiring, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('popstate'", wiring, StringComparison.Ordinal);
        Assert.Contains("applyMonitorFilters(readMonitorFilters(location.search), { writeUrl: false, broadcast: true })", wiring, StringComparison.Ordinal);
        var apply = Slice(monitor, "function applyMonitorFilters", "function applyRemoteFilterMessage");
        Assert.Contains("clearTimeout(filterTextTimer)", apply, StringComparison.Ordinal);
        Assert.Contains("if (options.writeUrl) writeMonitorUrl();", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyLink_FlushesPendingFilterText()
    {
        var monitor = Read(MonitorFile);
        Assert.Contains("let pendingFilterText = null;", monitor, StringComparison.Ordinal);
        var wiring = Slice(monitor, "function wireEvents", "ensureMonitorControls()");
        Assert.Contains("pendingFilterText = value;", wiring, StringComparison.Ordinal);
        Assert.Contains("pendingFilterText = null;", wiring, StringComparison.Ordinal);
        var copy = Slice(monitor, "async function copyFilterLink", "\n}");
        Assert.Contains("if (filterTextTimer) {", copy, StringComparison.Ordinal);
        Assert.Contains("applyMonitorFilters(", copy, StringComparison.Ordinal);
        Assert.True(copy.IndexOf("applyMonitorFilters(", StringComparison.Ordinal) <
                    copy.IndexOf("const url = monitorFilterUrl();", StringComparison.Ordinal),
            "복사 URL은 보류 중인 검색어 반영 뒤에 만들어야 합니다.");
        Assert.Contains("pendingFilterText = null;", Slice(monitor, "function applyMonitorFilters", "function applyRemoteFilterMessage"), StringComparison.Ordinal);
    }

    [Fact]
    public void BroadcastSourceIdGuard_AndRemoteApply()
    {
        var monitor = Read(MonitorFile);
        Assert.Contains("const MONITOR_FILTER_CHANNEL = 'updateagent-monitor-filters';", monitor, StringComparison.Ordinal);
        Assert.Contains("globalThis.crypto?.randomUUID", monitor, StringComparison.Ordinal);
        var remote = Slice(monitor, "function applyRemoteFilterMessage", "function initFilterSync");
        Assert.Contains("message.sourceId === FILTER_SOURCE_ID", remote, StringComparison.Ordinal);
        Assert.Contains("typeof message.at !== 'number'", remote, StringComparison.Ordinal);
        Assert.Contains("const FILTER_MESSAGE_SKEW_MS = 60000;", monitor, StringComparison.Ordinal);
        Assert.Contains("message.at > Date.now() + FILTER_MESSAGE_SKEW_MS", remote, StringComparison.Ordinal);
        Assert.Contains("{ writeUrl: true, broadcast: false }", remote, StringComparison.Ordinal);
        var sync = Slice(monitor, "function initFilterSync", "async function copyFilterLink");
        Assert.Contains("typeof BroadcastChannel !== 'undefined'", sync, StringComparison.Ordinal);
        Assert.Contains("filterBroadcast = new BroadcastChannel(MONITOR_FILTER_CHANNEL)", sync, StringComparison.Ordinal);
        Assert.Contains("event.key !== MONITOR_FILTER_STORAGE_KEY", sync, StringComparison.Ordinal);
        Assert.Contains("if (filterBroadcast ||", sync, StringComparison.Ordinal);
        Assert.Contains("applyRemoteFilterMessage(payload)", sync, StringComparison.Ordinal);
        Assert.Contains("if (!filterBroadcast) return;", Slice(monitor, "function broadcastFilters", "function saveFilterState"), StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardFailure_ShowsUnavailableGuidance()
    {
        var monitor = Read(MonitorFile);
        var copy = Slice(monitor, "async function copyFilterLink", "\n}");
        Assert.Contains("navigator.clipboard?.writeText", copy, StringComparison.Ordinal);
        Assert.Contains("try {", copy, StringComparison.Ordinal);
        Assert.Contains("} catch {", copy, StringComparison.Ordinal);
        Assert.Contains("링크 복사 확인 불가", copy, StringComparison.Ordinal);
        Assert.Contains("주소창 URL을 직접 복사하세요.", copy, StringComparison.Ordinal);
        Assert.Contains("필터 링크를 복사했습니다.", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("adminToken", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("STORAGE_KEY", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstAllowedUrlValueWins_OverUnknownValues()
    {
        var app = Read(DashboardFile);
        var monitor = Read(MonitorFile);
        Assert.Contains(".find((value) => value !== 'all')", app, StringComparison.Ordinal);
        Assert.Contains(".find((value) => value !== '')", monitor, StringComparison.Ordinal);
        Assert.Contains(".find((value) => value !== 'all')", monitor, StringComparison.Ordinal);
        var allowed = ReadOsGroups(app).Append("all").ToArray();
        // 실제로 보고된 허용 값은 그대로 사용하고, 먼저 온 알 수 없는 값은 건너뛴다.
        var osValues = new[] { "bogus", "SERVER" }.Select(value => ParseOs(value, allowed)).ToArray();
        Assert.Equal("server", osValues.First(value => value != "all"));
        var stateValues = new[] { "bogus", "BUSY" }.Select(value => ParseState(value, MonitorStates)).ToArray();
        Assert.Equal("busy", stateValues.First(value => value != "all"));
        var textValues = new[] { "bad!", "PC-01" }.Select(value => ParseText(value, false)).ToArray();
        Assert.Equal("pc-01", textValues.First(value => value != string.Empty));
    }

    [Fact]
    public void JobDeepLinkAndNoQueryDefaults_ArePreserved()
    {
        var app = Read(DashboardFile);
        var monitor = Read(MonitorFile);
        Assert.Contains("pendingJobId = new URLSearchParams(location.search).get('job');", app, StringComparison.Ordinal);
        Assert.Contains("consumeDeepLink();", app, StringComparison.Ordinal);
        Assert.Contains("function consumeDeepLink()", app, StringComparison.Ordinal);
        Assert.Contains("applyDashboardFilters(readDashboardFilters(location.search), { writeUrl: false });", app, StringComparison.Ordinal);
        Assert.Contains("applyMonitorFilters(readMonitorFilters(location.search), { writeUrl: false, broadcast: false });", monitor, StringComparison.Ordinal);
        Assert.Equal("all", ParseOs(string.Empty, ReadOsGroups(app).Append("all").ToArray()));
        Assert.Equal(string.Empty, ParseText(string.Empty, false));
        Assert.Equal("all", ParseState(string.Empty, MonitorStates));
        Assert.Contains("const url = jobId ? `/?job=${encodeURIComponent(jobId)}` : '/';", monitor, StringComparison.Ordinal);
        Assert.Contains("safe.append(key, value);", Slice(app, "function safeFilterParams", "function dashboardFilterUrl"), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMessage_RejectsInvalidTimestamp()
    {
        var monitor = Read(MonitorFile);
        var remote = Slice(monitor, "function applyRemoteFilterMessage", "function initFilterSync");
        Assert.Contains("Number.isFinite(message.at)", remote, StringComparison.Ordinal);
        Assert.Contains("message.at < 0", remote, StringComparison.Ordinal);
        Assert.Contains("message.at > Date.now() + FILTER_MESSAGE_SKEW_MS", remote, StringComparison.Ordinal);
        // 미래 시각/음수/문자열 시각은 실제 값이 아니라 거부 대상이다.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(IsValidTimestamp((double)now, now));
        Assert.False(IsValidTimestamp(-1d, now));
        Assert.False(IsValidTimestamp("not-a-time", now));
        Assert.False(IsValidTimestamp(double.PositiveInfinity, now));
        Assert.False(IsValidTimestamp((double)(now + 120000), now));
    }

    [Fact]
    public void NoBroadcastChannel_IsInert()
    {
        var monitor = Read(MonitorFile);
        var init = Slice(monitor, "function initFilterSync", "async function copyFilterLink");
        Assert.Contains("if (typeof BroadcastChannel !== 'undefined')", init, StringComparison.Ordinal);
        Assert.Contains("} catch {\n      filterBroadcast = null;", init, StringComparison.Ordinal);
        Assert.Contains("if (filterBroadcast ||", init, StringComparison.Ordinal);
        Assert.Contains("if (!filterBroadcast) return;", Slice(monitor, "function broadcastFilters", "function saveFilterState"), StringComparison.Ordinal);
        Assert.Equal("all", ParseState(string.Empty, MonitorStates));
        Assert.Equal(string.Empty, ParseText(string.Empty, false));
    }

    [Fact]
    public void DashboardSerialization_PreservesBenignParams()
    {
        var app = Read(DashboardFile);
        var url = Slice(app, "function dashboardFilterUrl", "function writeDashboardUrl");
        Assert.Contains("const params = safeFilterParams(search);", url, StringComparison.Ordinal);
        Assert.Contains("params.delete('os')", url, StringComparison.Ordinal);
        Assert.Contains("if (osFilter !== 'all') params.set('os', osFilter);", url, StringComparison.Ordinal);
        Assert.Contains("location.pathname", url, StringComparison.Ordinal);
        Assert.Contains("location.hash", url, StringComparison.Ordinal);
        var safe = SafeParameters([("job", "j1"), ("from", "dashboard"), ("os", "old")], "secret-value");
        Assert.Equal(3, safe.Count);
        // URL 빌더는 허용된 필터 키만 다시 쓴다.
        var withFilter = safe.Where(pair => pair.Key != "os").ToList();
        Assert.Equal(2, withFilter.Count);
        Assert.Contains(withFilter, pair => pair.Key == "job" && pair.Value == "j1");
        Assert.Contains(withFilter, pair => pair.Key == "from" && pair.Value == "dashboard");
    }

    [Fact]
    public void MonitorSerialization_PreservesBenignParams()
    {
        var monitor = Read(MonitorFile);
        var url = Slice(monitor, "function monitorFilterUrl", "function writeMonitorUrl");
        Assert.Contains("const params = safeFilterParams(search);", url, StringComparison.Ordinal);
        Assert.Contains("params.delete('q')", url, StringComparison.Ordinal);
        Assert.Contains("params.delete('state')", url, StringComparison.Ordinal);
        Assert.Contains("if (filterText) params.set('q', filterText);", url, StringComparison.Ordinal);
        Assert.Contains("if (stateFilter !== 'all') params.set('state', stateFilter);", url, StringComparison.Ordinal);
        var safe = SafeParameters([("job", "j1"), ("q", "bad!"), ("state", "bogus")], "secret-value");
        Assert.Equal(3, safe.Count);
        // URL 빌더는 q/state를 지우고 정규화된 값만 다시 쓴다.
        var withFilter = safe.Where(pair => pair.Key is not ("q" or "state")).ToList();
        Assert.Single(withFilter);
        Assert.Equal("job", withFilter[0].Key);
    }

    [Fact]
    public void DashboardChipClick_UsesNormalizedValue_AndRewritesFilterUrl()
    {
        var app = Read(DashboardFile);
        var render = Slice(app, "function renderOsFilter", "function updateDashboardFilterNotice");
        Assert.Contains("osFilter = normalizeOsFilterValue(button.dataset.osFilter);", render, StringComparison.Ordinal);
        Assert.Contains("osFilterUnknown = false;", render, StringComparison.Ordinal);
        Assert.Contains("writeDashboardUrl();", render, StringComparison.Ordinal);
        Assert.Contains("renderAgents();", render, StringComparison.Ordinal);
        Assert.DoesNotContain("location.href", render, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorRemoteApply_UpdatesUrlAndControls_WithoutRebroadcast()
    {
        var monitor = Read(MonitorFile);
        var apply = Slice(monitor, "function applyMonitorFilters", "function applyRemoteFilterMessage");
        foreach (var marker in new[]
                 {
                     "syncFilterControls();", "updateFilterNotice();", "renderMonitorTables();",
                     "if (options.writeUrl) writeMonitorUrl();", "if (options.broadcast)",
                     "broadcastFilters();",
                 })
        {
            Assert.Contains(marker, apply, StringComparison.Ordinal);
        }
        var remote = Slice(monitor, "function applyRemoteFilterMessage", "function initFilterSync");
        Assert.Contains("{ writeUrl: true, broadcast: false }", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("broadcastFilters()", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterControls_AreAddedWithoutHtmlChanges()
    {
        var monitor = Read(MonitorFile);
        var html = Read("src/UpdateAgent.LabManager/wwwroot/monitor.html");
        Assert.DoesNotContain("filter-link-copy", html, StringComparison.Ordinal);
        Assert.Contains("id = 'filter-link-copy'", monitor, StringComparison.Ordinal);
        Assert.Contains("'필터 링크 복사'", monitor, StringComparison.Ordinal);
        Assert.Contains("id = 'state-filter'", monitor, StringComparison.Ordinal);
        Assert.Contains("id = 'filter-notice'", monitor, StringComparison.Ordinal);
        Assert.Contains("id=\"os-filter\"", Read("src/UpdateAgent.LabManager/wwwroot/index.html"));
        var css = Read("src/UpdateAgent.LabManager/wwwroot/styles.css");
        Assert.Contains(".filter-share", css, StringComparison.Ordinal);
    }

    [Fact]
    public void WorklogAndDeployment_DocumentImprovement07()
    {
        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 7 — 대시보드 필터 URL 공유 / 다중 모니터 동기화";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 6", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 7은 개선 6 위에 있어야 합니다.");
        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);
        var previous = -1;
        foreach (var dimension in new[]
                 {
                     "요구사항 정합성", "하위 호환성", "토큰/비밀 배제", "URL 파싱 허용 목록과 길이 제한",
                     "히스토리/popstate/디바운스 정확성", "창 간 에코 방지와 원격 적용",
                     "클립보드 실패·알 수 없는 값", "한국어 문서/UI 정합성", "회귀 범위", "최종 검증",
                 })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }

        var deployment = Read("docs/DEPLOYMENT.md");
        var sectionStart = deployment.IndexOf("### 8-H.", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0);
        var sectionEnd = deployment.IndexOf("## 9. 보안 주의", sectionStart, StringComparison.Ordinal);
        var runbook = deployment[sectionStart..sectionEnd];
        foreach (var keyword in new[]
                 {
                     "?os=", "?q=", "?state=", "필터 링크 복사", "BroadcastChannel", "storage",
                     "source id", "확인 불가", "없음", "절대 URL", "알 수 없는 필터 값",
                 })
        {
            Assert.Contains(keyword, runbook, StringComparison.Ordinal);
        }
    }

    private static string[] ReadOsGroups(string app)
    {
        var groups = Slice(app, "const OS_GROUPS = [", "];");
        return Regex.Matches(groups, @"\{ id: '([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string[] ExtractArray(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"배열 선언을 찾지 못했습니다: {marker}");
        var open = source.IndexOf('[', start);
        var close = source.IndexOf(']', open);
        return Regex.Matches(source[(open + 1)..close], "'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string[] ExtractObjectKeys(string slice, string startMarker)
    {
        var marker = slice.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(marker >= 0, $"객체 시작을 찾지 못했습니다: {startMarker}");
        var open = slice.IndexOf('{', marker);
        var close = slice.IndexOf('}', open);
        Assert.True(open >= 0 && close > open);
        return Regex.Matches(slice[(open + 1)..close], @"(\w+)\s*:")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string ParseOs(string raw, string[] allowed)
    {
        var text = raw.Trim().ToLowerInvariant();
        return text.Length == 0 || text.Length > ValueMax || !allowed.Contains(text) ? "all" : text;
    }

    private static string ParseState(string raw, string[] allowed)
    {
        var text = raw.Trim().ToLowerInvariant();
        return allowed.Contains(text) ? text : "all";
    }

    private static bool IsValidTimestamp(object? value, long nowMs) =>
        value is double number &&
        double.IsFinite(number) &&
        number >= 0 &&
        number <= nowMs + FILTER_SKEW_MS;

    private const long FILTER_SKEW_MS = 60000;

    private static string ParseText(string raw, bool rejectSecrets)
    {
        var text = raw.Trim().ToLowerInvariant();
        if (text.Length == 0 || text.Length > ValueMax) return string.Empty;
        if (!Regex.IsMatch(text, @"^[\p{L}\p{N} ._-]+$")) return string.Empty;
        if (rejectSecrets && Regex.IsMatch(text, SecretKeyRegex, RegexOptions.IgnoreCase)) return string.Empty;
        return text;
    }

    private static List<KeyValuePair<string, string>> SafeParameters(
        IEnumerable<(string Key, string Value)> entries, string token)
    {
        var sensitive = new Regex(SecretKeyRegex, RegexOptions.IgnoreCase);
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in entries)
        {
            if (sensitive.IsMatch(key)) continue;
            if (sensitive.IsMatch(value)) continue;
            if (value.Length > ValueMax * 4) continue;
            if (!string.IsNullOrEmpty(token) && value == token) continue;
            result.Add(new KeyValuePair<string, string>(key, value));
        }
        return result;
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"구간을 찾지 못했습니다: {startMarker}");
        return text[start..end];
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, System.Text.Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
