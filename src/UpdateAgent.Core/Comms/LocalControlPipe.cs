using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Core.Comms;

/// <summary>
/// 학생용 상태 앱의 요청을 받아 서비스 작업 러너로 전달하는 로컬 제어 파이프 서버.
/// 연결마다 요청 한 줄과 응답 한 줄만 주고받는다.
/// </summary>
public sealed class LocalControlPipeServer : IAsyncDisposable
{
    internal const int MaxRequestBytes = 8192;
    internal const string ResponseTimeoutMessage =
        "서비스 응답 시간이 초과되었습니다. 상태를 새로고침해 작업 시작 여부를 확인하세요.";

    // 사전 점검/WUA가 느린 PC에서도 응답을 기다릴 수 있도록 클라이언트보다 서버를 먼저 끝낸다.
    // 테스트에서 짧게 조정할 수 있도록 readonly 대신 속성을 사용한다.
    internal static TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(90);
    internal static TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(95);

    private readonly Func<LocalControlRequest, CancellationToken, Task<LocalControlResponse>> _handler;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public LocalControlPipeServer(
        Func<LocalControlRequest, CancellationToken, Task<LocalControlResponse>> handler,
        string? pipeName = null)
    {
        _handler = handler;
        _pipeName = pipeName ?? UpdateAgentConstants.LocalControlPipeName;
    }

    /// <summary>요청이 성공하면 체크인을 앞당기기 위한 알림.</summary>
    public event Action? RequestSucceeded;

    public void Start() => _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));

    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        // 학생 계정은 로컬 관리자로 배포된다. UAC 필터링 토큰에서도 상태 앱이
        // 연결될 수 있도록 인증 사용자에게 읽기-쓰기를 허용한다.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity: CreatePipeSecurity());

                await pipe.WaitForConnectionAsync(cancellationToken);

                var connected = pipe;
                pipe = null; // 연결 소유권은 처리 태스크로 넘어간다.
                // 이미 취소된 토큰을 Task.Run에 넘기면 대리자가 실행되지 않아 연결 핸들이 새므로
                // 취소 여부는 핸들러가 linked token으로 판단하게 한다.
                _ = Task.Run(() => HandleClientAsync(connected, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                await Task.Delay(1000, CancellationToken.None);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            timeout.CancelAfter(HandlerTimeout);

            try
            {
                var line = await PipeLines.ReadLineAsync(pipe, MaxRequestBytes, timeout.Token)
                    .ConfigureAwait(false);
                if (line is null) return;

                LocalControlRequest? request;
                try
                {
                    request = UpdateAgentJson.Deserialize<LocalControlRequest>(line);
                }
                catch
                {
                    request = null;
                }

                var response = request is null
                    ? new LocalControlResponse
                    {
                        Success = false,
                        Message = "요청 형식이 올바르지 않습니다.",
                    }
                    : await _handler(request, timeout.Token).ConfigureAwait(false);

                // 작업이 저장된 뒤 응답 쓰기가 취소되더라도 즉시 체크인 알림은 보낸다.
                if (response.Success)
                {
                    try { RequestSucceeded?.Invoke(); }
                    catch { /* 구독자 오류는 파이프 응답에 영향을 주지 않는다. */ }
                }

                await PipeLines.WriteLineAsync(
                    pipe,
                    UpdateAgentJson.Serialize(response),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
            {
                // 서비스 종료가 아닌 핸들러 시간 초과. 연결만 끊으면 클라이언트가
                // 단순 연결 실패로 오해하므로 원인을 알리는 응답을 먼저 보낸다.
                try
                {
                    using var replyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await PipeLines.WriteLineAsync(
                        pipe,
                        UpdateAgentJson.Serialize(new LocalControlResponse
                        {
                            Success = false,
                            Message = ResponseTimeoutMessage,
                        }),
                        replyTimeout.Token).ConfigureAwait(false);
                }
                catch { /* 이미 닫힌 연결이면 무시한다. */ }
            }
            catch (OperationCanceledException)
            {
                // 서비스 종료.
            }
            catch
            {
                // 연결 종료/오류는 정상 흐름이다.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}

/// <summary>학생용 상태 앱이 로컬 제어 파이프에 요청을 보내는 클라이언트.</summary>
public sealed class LocalControlPipeClient
{
    private readonly string _pipeName;

    public LocalControlPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? UpdateAgentConstants.LocalControlPipeName;
    }

    public async Task<LocalControlResponse> SendAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // 시작은 느린 WUA 점검을 끝까지 기다리고, 긴급 중단은 오버레이가 즉시 사라지도록 짧게 기다린다.
            var responseTimeout = string.Equals(
                request.Action, LocalControlActions.CancelUpdate, StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(10)
                : LocalControlPipeServer.ResponseTimeout;
            timeout.CancelAfter(responseTimeout);

            await PipeLines.WriteLineAsync(
                pipe,
                UpdateAgentJson.Serialize(request),
                timeout.Token).ConfigureAwait(false);

            var line = await PipeLines.ReadLineAsync(pipe, LocalControlPipeServer.MaxRequestBytes, timeout.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                return new LocalControlResponse { Success = false, Message = "서비스에서 응답이 없습니다." };
            }

            return UpdateAgentJson.Deserialize<LocalControlResponse>(line)
                   ?? new LocalControlResponse { Success = false, Message = "서비스 응답을 해석할 수 없습니다." };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 호출자 취소가 아닌 내부 응답 시간 초과. 서비스가 작업을 시작했을 수 있으므로
            // 단순 연결 실패와 구분해 상태 확인을 안내한다.
            return new LocalControlResponse
            {
                Success = false,
                Message = LocalControlPipeServer.ResponseTimeoutMessage,
            };
        }
        catch (Exception ex)
        {
            return new LocalControlResponse
            {
                Success = false,
                Message = "서비스에 연결할 수 없습니다: " + ex.Message,
            };
        }
        finally
        {
            pipe?.Dispose();
        }
    }
}

internal static class PipeLines
{
    /// <summary>개행까지 최대 바이트만 읽는다. 악의적인 대용량 요청으로 메모리를 채우지 않는다.</summary>
    internal static async Task<string?> ReadLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer[count] == (byte)'\n') break;
            count++;
        }

        if (count == 0) return null;
        var length = buffer[count - 1] == (byte)'\r' ? count - 1 : count;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    internal static async Task WriteLineAsync(
        Stream stream,
        string line,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
