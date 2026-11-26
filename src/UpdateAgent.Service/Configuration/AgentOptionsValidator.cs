using Microsoft.Extensions.Options;
using UpdateAgent.Guards;
using UpdateAgent.Shared;

namespace UpdateAgent.Service.Configuration;

/// <summary>
/// 원격 관리가 켜져 있으면 공개된 기본/샘플 토큰으로 에이전트가 시작되지 않도록 막는다.
/// 토큰 값은 명령줄이 아니라 agent.json에서만 읽으므로 설정 오류를 시작 시점에 발견한다.
/// </summary>
public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Agent 설정이 없습니다.");
        }

        // 로컬 전용 모드(CheckInEnabled=false)에서는 토큰이 필요하지 않다.
        if (!options.RemoteEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (UpdateAgentConstants.IsPlaceholderToken(options.AgentToken))
        {
            return ValidateOptionsResult.Fail(
                "AgentToken이 기본값/샘플입니다. agent.json에 실제 토큰을 설정하세요. " +
                "토큰 값은 명령줄에 넣지 말고 파일로만 전달하세요.");
        }

        // 길이가 짧은 토큰은 서버에서 항상 인증 거부되므로 시작 시점에 알려준다.
        if (!TokenGuard.IsAcceptable(options.AgentToken))
        {
            return ValidateOptionsResult.Fail(
                $"AgentToken은 {TokenGuard.MinimumLength}자 이상이어야 합니다.");
        }

        // 잘못된 주소는 시작 시점에 알려주지 않으면 매 체크인이 조용히 실패한다.
        if (!UrlGuard.IsHttpUrl(options.LabManagerUrl, out var uri) ||
            uri is null ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ValidateOptionsResult.Fail(
                "LabManagerUrl은 사용자 정보가 없는 http(s) 절대 주소여야 합니다.");
        }

        // 음수/0이면 Worker의 Take()가 예외를 던져 원격 명령을 영구히 처리하지 못한다.
        if (options.MaxCommandsPerCheckIn is < 1 or > 100)
        {
            return ValidateOptionsResult.Fail("MaxCommandsPerCheckIn은 1~100이어야 합니다.");
        }

        return ValidateOptionsResult.Success;
    }
}
