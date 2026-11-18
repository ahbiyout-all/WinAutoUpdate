using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Core.Comms;

/// <summary>서비스가 호스팅하는 오버레이 파이프 서버. 연결된 모든 SessionHelper로 메시지를 보낸다.</summary>
public sealed class OverlayPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public OverlayPipeServer(string? pipeName = null)
    {
        _pipeName = pipeName ?? UpdateAgentConstants.OverlayPipeName;
    }

    public int ConnectedClients => _clients.Count;

    /// <summary>새 헬퍼가 연결되면 현재 진행 상태로 화면을 다시 맞추기 위한 알림.</summary>
    public event Action? ClientConnected;

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private static PipeSecurity CreatePipeSecurity()
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
        // 학생 계정은 로컬 관리자로 배포된다. UAC 필터링으로 관리자 그룹이
        // 비활성화된 토큰에서도 helper가 연결될 수 있도록 인증 사용자도 허용한다.
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
                // 기본 파이프 DACL은 Everyone에 읽기만 허용하므로 사용자 세션 helper가
                // InOut 클라이언트를 열 수 없다. SYSTEM/관리자/인증 사용자에게 명시적으로
                // 읽기-쓰기를 허용해 오버레이 연결이 실제로 동작하도록 한다.
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

                var id = Guid.NewGuid();
                var connection = new ClientConnection(pipe);
                _clients[id] = connection;
                try
                {
                    ClientConnected?.Invoke();
                }
                catch
                {
                    // 구독자의 실패가 파이프 수용 루프를 중단하지 않게 한다.
                }

                _ = Task.Run(() => HandleClientAsync(id, connection, cancellationToken), cancellationToken);
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

    private async Task HandleClientAsync(Guid id, ClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(
                connection.Pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested && connection.Pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
            }
        }
        catch
        {
            // 연결 종료/오류는 정상 흐름이다.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            connection.Dispose();
        }
    }

    public async Task BroadcastAsync(OverlayMessage message, CancellationToken cancellationToken = default)
    {
        if (_clients.IsEmpty) return;

        var json = UpdateAgentJson.Serialize(message);
        foreach (var (id, client) in _clients)
        {
            try
            {
                await client.WriteAsync(json, cancellationToken);
            }
            catch
            {
                _clients.TryRemove(id, out _);
                client.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientConnection(NamedPipeServerStream pipe)
        {
            Pipe = pipe;
            Writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
        }

        public NamedPipeServerStream Pipe { get; }
        public StreamWriter Writer { get; }

        public async Task WriteAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await Writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            try { Writer.Dispose(); } catch { }
            try { Pipe.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }
}

/// <summary>SessionHelper가 사용하는 파이프 클라이언트. 연결이 끊기면 자동 재연결한다.</summary>
public sealed class OverlayPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public OverlayPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? UpdateAgentConstants.OverlayPipeName;
    }

    public event Func<OverlayMessage, Task>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        while (!linked.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(3000, linked.Token);
                ConnectionChanged?.Invoke(true);

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);

                while (!linked.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(linked.Token);
                    if (line is null) break;

                    OverlayMessage? message;
                    try
                    {
                        message = UpdateAgentJson.Deserialize<OverlayMessage>(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (message is not null && MessageReceived is not null)
                    {
                        await MessageReceived(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 서비스가 아직 준비되지 않았거나 종료됨 → 재시도
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                pipe?.Dispose();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
