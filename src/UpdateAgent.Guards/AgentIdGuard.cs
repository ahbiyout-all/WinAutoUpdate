namespace UpdateAgent.Guards;

/// <summary>
/// 에이전트 ID의 단일 정규화 규칙. 저장·조회·정책·명령 경로가 모두 같은 값을 사용해야
/// 대소문자만 다른 중복 행이나 전달되지 않는 명령이 생기지 않는다.
/// </summary>
public static class AgentIdGuard
{
    public static string Canonical(string? agentId) =>
        TokenGuard.Normalize(agentId).ToLowerInvariant();
}
